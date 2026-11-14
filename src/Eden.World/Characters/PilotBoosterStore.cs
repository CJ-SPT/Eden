using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.World.Characters;

[Injectable]
internal sealed class PilotBoosterStore(WorldDatabase database, PilotTrainingStore training, TimeProvider clock) : IPilotBoosters
{
    internal static ActiveBooster[] Active(WorldAction world, long pilot, long now) =>
        world
            .Filter<SavedBooster>(b => b.PilotId == pilot && b.ExpiresAt > now)
            .OrderBy(b => b.Slot)
            .AsEnumerable()
            .Select(b => b.Snapshot())
            .ToArray();

    internal static BoosterSnapshot Read(WorldAction world, long pilot, long now)
    {
        var boosters = Active(world, pilot, now);
        return new(
            boosters,
            world.Set<SavedBoosterState>().Find(pilot)?.Revision ?? 0,
            BoosterRules.Effective(PilotImplantStore.Read(world, pilot).Attributes, boosters)
        );
    }

    public BoosterSnapshot Read(long pilotId)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("read.boosters");
        if (LiveReadModels.Find(database.Path) is { } resident)
        {
            return resident.Learning(pilotId).At(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        }
        return WorldActionScope.Inspect(
            database,
            world => Read(world, pilotId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()),
            consistentFileRead: true
        );
    }

    private static CharacterBoosterProfile Profile(CatalogDbContext catalog, InventoryItem item)
    {
        var published = catalog.Read(CatalogData.Client.Types, (long)item.TypeId)?.Published;
        var effects = catalog.Read(CatalogData.Client.Typedogma, (long)item.TypeId)?.DogmaEffects;
        double useAttribute = catalog.FittingAttributes(item.TypeId).GetValueOrDefault(DogmaAttributeId.NonDiminishingSkillInjectorUses, 0);
        bool augmentor = useAttribute > 0 && effects is { Length: 0 };
        // Pure learning effects or the catalog's dedicated injection-use booster.
        if (
            item.GroupId != BoosterRules.Group
            || item.CategoryId != (int)InventoryCategory.Implant
            || published != 1
            || effects is null
            || (!augmentor && (useAttribute != 0 || !PilotImplantStore.IsLearningEffects(effects.Value)))
        )
        {
            throw new InvalidDataException(
                "Only published learning or injection boosters are supported. Combat booster effects are unavailable."
            );
        }
        var attributes = catalog.FittingAttributes(item.TypeId);
        double Value(DogmaAttributeId id) => attributes.GetValueOrDefault(id);
        int Integer(DogmaAttributeId id)
        {
            double value = Value(id);
            return double.IsFinite(value) && value is > 0 and <= int.MaxValue && value == Math.Truncate(value)
                ? checked((int)value)
                : throw new InvalidDataException("Invalid cerebral accelerator attribute.");
        }
        int uses = augmentor ? Integer(DogmaAttributeId.NonDiminishingSkillInjectorUses) : 0;
        int bonus = augmentor ? 0 : Integer(DogmaAttributeId.CharismaBonus);
        if (
            Value(DogmaAttributeId.CharismaBonus) != bonus
            || Value(DogmaAttributeId.IntelligenceBonus) != bonus
            || Value(DogmaAttributeId.MemoryBonus) != bonus
            || Value(DogmaAttributeId.PerceptionBonus) != bonus
            || Value(DogmaAttributeId.WillpowerBonus) != bonus
            || Value(DogmaAttributeId.NonDestructible) != 1
            || Value(DogmaAttributeId.FollowsJumpClones) != 1
        )
        {
            throw new InvalidDataException("Unsupported cerebral accelerator bonus or clone behavior.");
        }
        long Ticks(DogmaAttributeId id, long units)
        {
            double value = Value(id);
            if (!double.IsFinite(value) || value < 0 || value > long.MaxValue / (double)units)
            {
                throw new InvalidDataException("Invalid cerebral accelerator lifetime.");
            }
            return checked((long)Math.Round(value * units, MidpointRounding.AwayFromZero));
        }
        long age = Ticks(DogmaAttributeId.BoosterMaximumAgeHours, TimeSpan.TicksPerHour);
        // Pinned dogma/attributes/datetime: Unix-epoch days, rounded to whole seconds.
        // This is the last consumption date, independent of the active effect's expiry.
        long? last = Value(DogmaAttributeId.BoosterLastInjectionDate) is > 0
            ? checked(
                DateTime.UnixEpoch.ToFileTimeUtc()
                + (long)
                    Math.Round(
                        Value(DogmaAttributeId.BoosterLastInjectionDate) * TimeSpan.FromDays(1).TotalSeconds,
                        MidpointRounding.AwayFromZero
                    ) * TimeSpan.TicksPerSecond
            )
            : null;
        return new(
            item.TypeId,
            Integer(DogmaAttributeId.BoosterSlot),
            bonus,
            Ticks(DogmaAttributeId.BoosterDuration, TimeSpan.TicksPerMillisecond),
            age > 0 ? age : null,
            last
        )
        {
            NonDiminishingUses = uses,
        };
    }

    private static int Biology(WorldAction world, CatalogDbContext catalog, Pilot pilot)
    {
        int level = PilotSkillReader.Level(world, catalog, pilot.Id, BoosterRules.BiologySkill);
        if (pilot.Account.CloneGrade == AccountCloneGrade.Alpha)
        {
            var alpha = catalog.Require(CatalogData.Client.Clonegrades, pilot.RaceId.ToString(CultureInfo.InvariantCulture)).Skills!;
            level = Math.Min(level, checked((int)(alpha.Value.SingleOrDefault(s => s.TypeID == BoosterRules.BiologySkill)?.Level ?? 0)));
        }
        return level;
    }

    public BoosterChange Consume(long pilotId, long itemId, long expectedLocation)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Filter<Pilot>(p => p.Id == pilotId).Single();
        var item =
            InventoryPersistence.OwnedStack(world, pilotId, itemId)
            ?? throw new InvalidDataException("That booster does not belong to you.");
        var ship = pilot.ActiveShip;
        if (
            item.LocationId != expectedLocation
            || item.Quantity < 1
            || item.Singleton
            || !(
                (item.LocationId == ship.Id && item.FlagId == (int)InventoryFlag.Cargo)
                || (ship.Position is null && item.LocationId == ship.StationId && item.FlagId == (int)InventoryFlag.Hangar)
            )
        )
        {
            throw new InvalidDataException("Select a booster in your current cargo or docked station hangar.");
        }
        var previous = PilotInventoryStore.Read(catalog, item);
        var profile = Profile(catalog, previous);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var result = training.ChangeAttributes(
            world,
            pilotId,
            () =>
            {
                PilotImplantStore.Requirements(world, catalog, pilot, item.TypeId);
                long expiry = BoosterRules.Expiry(
                    profile,
                    pilot.CreatedAt,
                    Biology(world, catalog, pilot),
                    now,
                    Active(world, pilotId, now)
                );
                world.RemoveRange(world.Filter<SavedBooster>(b => b.PilotId == pilotId && b.Slot == profile.Slot).ToArray());
                world.SaveChanges();
                long installed = item.Quantity == 1 ? item.Id : world.NextItemId();
                item.Quantity--;
                if (item.Quantity == 0)
                {
                    world.Remove(item);
                }
                world.Add(
                    new SavedBooster
                    {
                        ItemId = installed,
                        PilotId = pilotId,
                        TypeId = item.TypeId,
                        Slot = profile.Slot,
                        Bonus = profile.Bonus,
                        RemainingInjectionUses = profile.NonDiminishingUses,
                        StartedAt = now,
                        ExpiresAt = expiry,
                    }
                );
                var revision =
                    world.Set<SavedBoosterState>().Find(pilotId) ?? world.Add(new SavedBoosterState { PilotId = pilotId }).Entity;
                revision.Revision = checked(revision.Revision + 1);
                if (profile.NonDiminishingUses > 0)
                {
                    var injections = PilotSkillInjectorStore.State(world, pilotId);
                    injections.Revision = checked(injections.Revision + 1);
                }
            },
            implantChange: false,
            effectiveTime: now
        );
        var snapshot = Read(world, pilotId, now);
        transaction.Commit();
        return new(snapshot, result, new(previous with { Quantity = item.Quantity }, previous));
    }
}
