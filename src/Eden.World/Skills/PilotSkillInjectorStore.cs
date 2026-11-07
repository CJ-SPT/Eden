using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Skills;

[Injectable]
internal sealed class PilotSkillInjectorStore(WorldDatabase database, PilotTrainingStore training, TimeProvider clock)
    : IPilotSkillInjectors
{
    private static Pilot Pilot(WorldDbContext world, long pilot) =>
        world.Pilots.Include(p => p.Account).Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == pilot)
        ?? throw new InvalidDataException("Unknown pilot.");

    internal static SavedSkillInjectorState State(WorldDbContext world, long pilot) =>
        world.Set<SavedSkillInjectorState>().Find(pilot) ?? world.Add(new SavedSkillInjectorState { PilotId = pilot }).Entity;

    internal static SkillInjectorStatus Read(WorldDbContext world, long pilot, long now)
    {
        var state = world.Set<SavedSkillInjectorState>().Find(pilot);
        int uses = world.Set<SavedBooster>().Where(b => b.PilotId == pilot && b.ExpiresAt > now).Sum(b => b.RemainingInjectionUses);
        return new(uses, state?.NextAlphaInjection ?? 0, state?.Revision ?? 0);
    }

    public SkillInjectorStatus Read(long pilotId)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("read.injectors");
        if (LiveReadModels.Find(database.Path) is { } resident)
        {
            return resident.Learning(pilotId).InjectionsAt(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        _ = Pilot(world, pilotId);
        return Read(world, pilotId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
    }

    private static void Station(Pilot pilot)
    {
        if (pilot.ActiveShip.Position is not null)
        {
            throw new InvalidSkillInjection("CharacterServiceItemNotInStation");
        }
    }

    private static OwnedItem Item(WorldDbContext world, Pilot pilot, long id, int quantity)
    {
        var item =
            InventoryPersistence.OwnedStack(world, pilot.Id, id) ?? throw new InvalidSkillInjection("YouDoNotOwnCharacterServiceItem");
        Station(pilot);
        // The pinned injector error text specifically requires the station hangar.
        if (item.LocationId != pilot.ActiveShip.StationId || item.FlagId != (int)InventoryFlag.Hangar)
        {
            throw new InvalidSkillInjection("CharacterServiceItemNotInStation");
        }
        if (quantity <= 0 || quantity > item.Quantity || item.Singleton)
        {
            throw new InvalidSkillInjection("InconsistentInjectorQuantity");
        }
        return item;
    }

    private static SkillInjectorProfile Profile(CatalogDbContext catalog, int id)
    {
        var type = catalog.FittingType(id);
        var published = catalog.Read(CatalogData.Client.Types, (long)id)?.Published;
        if (type?.GroupId != SkillInjectorRules.Group || published != 1)
        {
            throw new InvalidSkillInjection("IncorrectServiceItemType");
        }
        if (id == (int)SkillInjectorType.Large || id == (int)SkillInjectorType.Small)
        {
            return new(
                id,
                SkillInjectorKind.Diminishing,
                id == (int)SkillInjectorType.Large ? SkillInjectorRules.LargePoints : SkillInjectorRules.SmallPoints,
                null
            );
        }
        long Attribute(DogmaAttributeId attribute, bool required)
        {
            double value = catalog.FittingAttributes(id).GetValueOrDefault(attribute, 0);
            if (!double.IsFinite(value) || value < (required ? 1 : 0) || value >= long.MaxValue || value != Math.Truncate(value))
            {
                throw new InvalidSkillInjection("IncorrectServiceItemType");
            }
            return checked((long)value);
        }
        long points = Attribute(DogmaAttributeId.ContainedSkillPoints, true);
        long maximum = Attribute(DogmaAttributeId.MaximumCharacterSkillPoints, false);
        return new(
            id,
            id == (int)SkillInjectorType.DailyAlpha ? SkillInjectorKind.DailyAlpha : SkillInjectorKind.Fixed,
            points,
            maximum > 0 ? maximum : null
        );
    }

    private static long Allocated(WorldDbContext world, long pilot, TrainingState training, long now)
    {
        long total = world.Set<PilotSkill>().Where(s => s.PilotId == pilot).Sum(s => s.Points);
        if (training.Queue.FirstOrDefault() is { StartTime: not null } first)
        {
            total = checked(total + first.PointsAt(now) - world.Set<PilotSkill>().Find(pilot, first.Target.TypeId)!.Points);
        }
        return total;
    }

    public long AllocatedPoints(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        _ = Pilot(world, pilotId);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        return Allocated(world, pilotId, training.Advance(world, pilotId, now), now);
    }

    private static SkillInjectionPlan Plan(
        WorldDbContext world,
        Pilot pilot,
        SkillInjectorProfile profile,
        int quantity,
        TrainingState queue,
        long now
    )
    {
        var status = Read(world, pilot.Id, now);
        return SkillInjectorRules.Plan(
            profile,
            quantity,
            new(
                Allocated(world, pilot.Id, queue, now),
                PilotTrainingStore.ReadFreePoints(world, pilot.Id),
                pilot.Account.CloneGrade,
                status.AvailableUses,
                status.NextAlphaInjection
            ),
            now
        );
    }

    public long Preview(long pilotId, int typeId, int quantity)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        Station(pilot);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        return Plan(world, pilot, Profile(catalog, typeId), quantity, training.Advance(world, pilotId, now), now).Points;
    }

    public void Check(long pilotId, long itemId, int quantity)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var item = Item(world, pilot, itemId, quantity);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        _ = Plan(world, pilot, Profile(catalog, item.TypeId), quantity, training.Advance(world, pilotId, now), now);
    }

    public SkillInjectorChange Inject(long pilotId, long itemId, int quantity)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var item = Item(world, pilot, itemId, quantity);
        var profile = Profile(catalog, item.TypeId);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var settled = training.Advance(world, pilotId, now);
        var plan = Plan(world, pilot, profile, quantity, settled, now);
        var previous = PilotInventoryStore.Read(catalog, item);
        item.Quantity -= quantity;
        if (item.Quantity == 0)
        {
            world.Remove(item);
        }
        int remaining = plan.UsesSpent;
        foreach (
            var booster in world
                .Set<SavedBooster>()
                .Where(b => b.PilotId == pilotId && b.ExpiresAt > now && b.RemainingInjectionUses > 0)
                .OrderBy(b => b.ExpiresAt)
                .ThenBy(b => b.ItemId)
        )
        {
            int spent = Math.Min(remaining, booster.RemainingInjectionUses);
            booster.RemainingInjectionUses -= spent;
            remaining -= spent;
        }
        var state = State(world, pilotId);
        state.NextAlphaInjection = plan.NextAlphaInjection;
        state.Revision = checked(state.Revision + (plan.UsesSpent > 0 ? 1 : 0));
        world.Add(
            new SavedSkillInjection
            {
                PilotId = pilotId,
                ItemId = item.Id,
                TypeId = item.TypeId,
                Quantity = quantity,
                Points = plan.Points,
                TotalBefore = plan.TotalAfter - plan.Points,
                TotalAfter = plan.TotalAfter,
                UsesSpent = plan.UsesSpent,
                Time = now,
            }
        );
        var credited = training.Credit(world, pilotId, plan.Points, now);
        var result = credited with
        {
            Training = new(credited.Training.Queue, credited.Training.ChangedSkills.Union(settled.ChangedSkills).ToArray()),
        };
        world.SaveChanges();
        var status = Read(world, pilotId, now);
        transaction.Commit();
        return new(result, status, [new(previous with { Quantity = item.Quantity }, previous)]);
    }

    public IReadOnlyList<InventoryChange> Convert(long pilotId, long itemId, int quantity, bool split)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var item = Item(world, pilot, itemId, quantity);
        _ = Profile(catalog, item.TypeId);
        var plan = SkillInjectorRules.Conversion(item.TypeId, quantity, split);
        _ = Profile(catalog, plan.OutputType);
        var previous = PilotInventoryStore.Read(catalog, item);
        item.Quantity -= plan.Consumed;
        if (item.Quantity == 0)
        {
            world.Remove(item);
        }
        var output = InventoryPersistence.MergeOutput(
            world,
            catalog,
            pilotId,
            item.LocationId,
            item.FlagId,
            plan.OutputType,
            plan.OutputQuantity
        );
        world.SaveChanges();
        transaction.Commit();
        return [new(previous with { Quantity = item.Quantity }, previous), output];
    }
}
