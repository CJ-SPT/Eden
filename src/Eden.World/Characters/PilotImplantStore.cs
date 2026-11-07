using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Characters;

[Injectable]
internal sealed class PilotImplantStore(WorldDatabase database, PilotTrainingStore training) : IPilotImplants
{
    // Pinned typedogma: these five passive effects add the corresponding bonus
    // attributes. Require the complete pure-learning profile; set/hardwiring
    // effects must never be silently consumed without implementing their bonus.
    private enum LearningEffect
    {
        Charisma = 302,
        Memory = 304,
        Intelligence = 306,
        Willpower = 308,
        Perception = 310,
    }

    internal static bool IsLearningEffects(IEnumerable<ClientTypedogmaDogmaEffectsItem> effects) =>
        effects.Select(e => e.EffectID).ToHashSet().SetEquals(Enum.GetValues<LearningEffect>().Select(e => (long?)e));

    internal static ImplantSnapshot Read(WorldDbContext world, long pilotId)
    {
        var basis = PilotCharacterSheetStore.ReadAttributes(world, pilotId).Attributes;
        var implants = (
            from implant in world.Set<SavedPilotImplant>()
            join body in world.Set<SavedJumpClone>() on implant.CloneId equals body.Id
            where body.PilotId == pilotId && body.Active
            orderby implant.Slot
            select implant
        )
            .AsEnumerable()
            .Select(i => i.Snapshot())
            .ToArray();
        return new(implants, ImplantRules.Effective(basis, implants))
        {
            Revision = world.Set<SavedImplantState>().Find(pilotId)?.Revision ?? 0,
        };
    }

    public ImplantSnapshot Read(long pilotId)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("read.implants");
        if (LiveReadModels.Find(database.Path) is { } resident)
        {
            return resident.Learning(pilotId).Implants;
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        return Read(world, pilotId);
    }

    private static LearningImplant Profile(CatalogDbContext catalog, InventoryItem item)
    {
        string key = item.TypeId.ToString(CultureInfo.InvariantCulture);
        var type = catalog.Set<SourceRecord>().Find("client/types", key)?.Read<ClientTypes>();
        var effects = catalog.Set<SourceRecord>().Find("client/typedogma", key)?.Read<ClientTypedogma>().DogmaEffects;
        if (
            item.CategoryId != (int)InventoryCategory.Implant
            || item.GroupId != ImplantRules.LearningGroup
            || type?.Published != 1
            || effects is null
            || !IsLearningEffects(effects.Value)
        )
        {
            throw new InvalidDataException("Only published learning implants are supported. Hardwiring and set effects are unavailable.");
        }
        var attributes = catalog
            .Set<TypeAttribute>()
            .Where(a => a.ItemTypeId == item.TypeId)
            .ToDictionary(a => a.AttributeId, a => a.Value);
        double slot = attributes.GetValueOrDefault((int)DogmaAttributeId.ImplantSlot);
        if (slot is < 1 or > 5 || slot != Math.Truncate(slot))
        {
            throw new InvalidDataException("Invalid learning implant slot.");
        }
        (DogmaAttributeId BonusAttribute, DogmaAttributeId CharacterAttribute)[] bonuses =
        [
            (DogmaAttributeId.CharismaBonus, DogmaAttributeId.Charisma),
            (DogmaAttributeId.IntelligenceBonus, DogmaAttributeId.Intelligence),
            (DogmaAttributeId.MemoryBonus, DogmaAttributeId.Memory),
            (DogmaAttributeId.PerceptionBonus, DogmaAttributeId.Perception),
            (DogmaAttributeId.WillpowerBonus, DogmaAttributeId.Willpower),
        ];
        var nonzero = bonuses.Where(a => attributes.GetValueOrDefault((int)a.BonusAttribute) != 0).ToArray();
        if (nonzero.Length != 1)
        {
            throw new InvalidDataException("Invalid learning implant bonuses.");
        }
        double bonus = attributes[(int)nonzero[0].BonusAttribute];
        if (!double.IsFinite(bonus) || bonus != Math.Truncate(bonus) || bonus is < 1 or > ImplantRules.MaximumLearningBonus)
        {
            throw new InvalidDataException("Invalid learning implant bonus.");
        }
        return new(item.TypeId, checked((int)slot), nonzero[0].CharacterAttribute, checked((int)bonus));
    }

    internal static void Requirements(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, int typeId)
    {
        var attributes = catalog.Set<TypeAttribute>().Where(a => a.ItemTypeId == typeId).ToDictionary(a => a.AttributeId, a => a.Value);
        var alpha =
            pilot.Account.CloneGrade == AccountCloneGrade.Alpha
                ? CatalogData
                    .Client.Clonegrades.Get(catalog, pilot.RaceId.ToString(CultureInfo.InvariantCulture))
                    .Skills!.Value.ToDictionary(s => s.TypeID!.Value, s => s.Level!.Value)
                : null;
        List<TrainingTarget> requirements = [];
        Dictionary<int, int> levels = [];
        foreach (var (skillAttribute, levelAttribute) in InventoryDefinitions.RequiredSkills)
        {
            double skill = attributes.GetValueOrDefault((int)skillAttribute);
            if (skill == 0)
            {
                continue;
            }
            double required = attributes.GetValueOrDefault((int)levelAttribute);
            if (
                skill is < 1 or > int.MaxValue
                || skill != Math.Truncate(skill)
                || required is < 1 or > 5
                || required != Math.Truncate(required)
            )
            {
                throw new InvalidDataException("Invalid augmentation skill requirement.");
            }
            int type = checked((int)skill);
            requirements.Add(new(type, checked((int)required)));
            levels[type] = PilotSkillReader.Level(world, catalog, pilot.Id, type);
        }
        ImplantRules.CheckSkills(requirements, levels, alpha?.ToDictionary(p => checked((int)p.Key), p => checked((int)p.Value)));
    }

    public ImplantChange Install(long pilotId, long itemId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world
            .Pilots.Include(p => p.Account)
            .Include(p => p.ActiveShip)
                .ThenInclude(s => s.Position)
            .Single(p => p.Id == pilotId);
        var item =
            InventoryPersistence.OwnedStack(world, pilotId, itemId)
            ?? throw new InvalidDataException("That implant does not belong to you.");
        var ship = pilot.ActiveShip;
        if (
            !(
                (item.LocationId == ship.Id && item.FlagId == (int)InventoryFlag.Cargo)
                || (ship.Position is null && item.LocationId == ship.StationId && item.FlagId == (int)InventoryFlag.Hangar)
            )
            || item.Quantity < 1
            || item.Singleton
        )
        {
            throw new InvalidDataException("Select an implant in your current cargo or docked station hangar.");
        }
        var previous = PilotInventoryStore.Read(catalog, item);
        var profile = Profile(catalog, previous);
        ImplantRules.Check(profile, Read(world, pilotId).Implants);
        var state = training.ChangeAttributes(
            world,
            pilotId,
            () =>
            {
                Requirements(world, catalog, pilot, item.TypeId);
                var body =
                    world.Set<SavedJumpClone>().SingleOrDefault(c => c.PilotId == pilotId && c.Active)
                    ?? world.Add(new SavedJumpClone { PilotId = pilotId, Active = true }).Entity;
                world.SaveChanges();
                long installedId = item.Quantity == 1 ? item.Id : InventoryPersistence.NextId(world);
                item.Quantity--;
                if (item.Quantity == 0)
                {
                    world.Remove(item);
                }
                world.Add(
                    new SavedPilotImplant
                    {
                        ItemId = installedId,
                        CloneId = body.Id,
                        TypeId = profile.TypeId,
                        Slot = profile.Slot,
                        Attribute = profile.Attribute,
                        Bonus = profile.Bonus,
                    }
                );
            }
        );
        var snapshot = Read(world, pilotId);
        transaction.Commit();
        return new(snapshot, state, [new(previous with { Quantity = item.Quantity }, previous)]);
    }

    public ImplantChange Destroy(long pilotId, long itemId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var item =
            (
                from implant in world.Set<SavedPilotImplant>()
                join body in world.Set<SavedJumpClone>() on implant.CloneId equals body.Id
                where body.PilotId == pilotId && body.Active && implant.ItemId == itemId
                select implant
            ).SingleOrDefault() ?? throw new InvalidDataException("That implant is not installed in your active clone.");
        var state = training.ChangeAttributes(world, pilotId, () => world.Remove(item));
        var snapshot = Read(world, pilotId);
        transaction.Commit();
        return new(snapshot, state, []);
    }
}
