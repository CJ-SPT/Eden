using Eden.Data.Catalog.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Skills;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal static partial class TrafficFitting
{
    internal static void Apply(WorldAction world, CatalogDbContext catalog, long pilot, long ship, TrafficProfile profile, long now)
    {
        CheckTypes(catalog, profile);
        var hull = world.Ships.Find(ship) ?? throw new InvalidDataException("Missing traffic ship.");
        hull.TypeId = profile.HullTypeId;
        GrantSkills(world, catalog, pilot, profile);
        world.SaveChanges();
        // Fit rigs before bringing resource consumers online. The normal fitting
        // owner remains responsible for slots, hardpoints, calibration and effects.
        var modules = profile
            .Equipment.Where(item => catalog.RequireItem(item.TypeId).CategoryId != (int)InventoryCategory.Charge)
            .OrderByDescending(item => InventoryDefinitions.IsRigSlot(item.Flag))
            .ThenBy(item => item.Flag)
            .ToArray();
        Dictionary<long, int> placements = [];
        long nextItem = InventoryPersistence.NextId(world);
        foreach (var module in modules)
        {
            var item = new OwnedItem
            {
                Id = nextItem++,
                OwnerId = pilot,
                TypeId = module.TypeId,
                LocationId = hull.StationId,
                FlagId = (int)InventoryFlag.Hangar,
                Quantity = 1,
            };
            world.Add(item);
            placements.Add(item.Id, module.Flag);
        }
        world.SaveChanges();
        if (placements.Count > 0)
        {
            PilotFittingStore.Fit(
                world,
                catalog,
                pilot,
                placements.Keys.ToArray(),
                hull.StationId,
                (int)InventoryFlag.AutoFit,
                now,
                placements,
                completeOnline: true
            );
        }
        var fitted = world.Set<OwnedItem>().Where(item => item.LocationId == ship).OrderBy(item => item.FlagId).ToArray();
        var magazines = profile
            .Equipment.Where(item => catalog.RequireItem(item.TypeId).CategoryId == (int)InventoryCategory.Charge)
            .Select(charge => new Magazine(
                fitted.SingleOrDefault(item => item.FlagId == charge.Flag)
                    ?? throw new InvalidDataException("Loaded charge has no fitted module."),
                charge
            ))
            .GroupBy(magazine =>
            {
                var type = catalog.RequireItem(magazine.Charge.TypeId);
                double volume = PilotInventoryStore.Volume(catalog, type.Id);
                int full = checked(
                    (int)
                        Math.Floor(
                            (catalog.RequireItem(magazine.Module.TypeId).Capacity ?? 0) / volume + InventoryDefinitions.ResourceTolerance
                        )
                );
                if (AmmunitionDefinitions.IsCrystalGroup(type.GroupId) || SupportModuleDefinitions.IsScript(type.GroupId))
                {
                    full = Math.Min(full, 1);
                }
                // Full magazines can share one load. Partial magazines retain their exact per-slot quantities.
                return new MagazineBatch(type.Id, magazine.Charge.Quantity == full ? 0 : magazine.Charge.Flag);
            });
        foreach (var batch in magazines)
        {
            var targets = batch.ToArray();
            var item = new OwnedItem
            {
                Id = InventoryPersistence.NextId(world),
                OwnerId = pilot,
                TypeId = batch.Key.TypeId,
                LocationId = hull.StationId,
                FlagId = (int)InventoryFlag.Hangar,
                Quantity = targets.Sum(target => target.Charge.Quantity),
            };
            world.Add(item);
            world.SaveChanges();
            PilotAmmunitionStore.Load(
                world,
                catalog,
                pilot,
                ship,
                targets.Select(target => target.Module.Id).ToArray(),
                [item.Id],
                hull.StationId,
                now
            );
            var moduleIds = targets.Select(target => target.Module.Id).ToArray();
            var loaded = world
                .Set<OwnedItem>()
                .Where(value => Enumerable.Contains(moduleIds, value.LocationId))
                .ToDictionary(value => value.LocationId);
            if (
                targets.Any(target =>
                    !loaded.TryGetValue(target.Module.Id, out var charge)
                    || charge.TypeId != target.Charge.TypeId
                    || charge.Quantity != target.Charge.Quantity
                )
            )
            {
                throw new InvalidDataException("Recorded ammunition exceeds the legal magazine capacity.");
            }
        }
        world.SaveChanges();
        if (
            world
                .Set<OwnedItem>()
                .Any(item =>
                    item.LocationId == ship
                    && item.FlagId >= (int)InventoryFlag.LowSlot0
                    && item.FlagId <= (int)InventoryFlag.HighSlot7
                    && !item.Online
                )
        )
        {
            throw new InvalidDataException("The complete fitting cannot remain online.");
        }
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot);
        double used = InventoryPersistence.UsedVolume(world, catalog, pilot, ship, (int)InventoryFlag.Cargo);
        if (used > attributes.GetValueOrDefault(DogmaAttributeId.CargoCapacity) + InventoryDefinitions.ResourceTolerance)
        {
            throw new InvalidDataException("Existing cargo does not fit the selected traffic hull.");
        }
    }

    private static void GrantSkills(WorldAction world, CatalogDbContext catalog, long pilot, TrafficProfile profile)
    {
        HashSet<int> visited = [];
        HashSet<int> required = [];
        Collect(profile.HullTypeId);
        foreach (var item in profile.Equipment)
        {
            Collect(item.TypeId);
        }
        var skills = Enum.GetValues<FittingSkill>()
            .Select(skill => (int)skill)
            .Concat(Enum.GetValues<NavigationSkill>().Select(skill => (int)skill))
            .Concat(Enum.GetValues<RiggingSkill>().Select(skill => (int)skill))
            .Concat(Enum.GetValues<CombatSkill>().Select(skill => (int)skill))
            .Concat(Enum.GetValues<DefenseSkill>().Select(skill => (int)skill))
            .Concat(PassiveModules.Skills)
            .Concat(required)
            .Distinct();
        foreach (int skill in skills)
        {
            if (catalog.FittingAttributes(skill).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant) is not > 0)
            {
                continue;
            }
            long points = TrainedSkill.PointsForLevel(
                catalog.FittingAttributes(skill)[DogmaAttributeId.SkillTimeConstant],
                TrafficRules.MaximumSkillLevel
            );
            var saved = world.Set<PilotSkill>().Find(pilot, skill) ?? world.Add(new PilotSkill { PilotId = pilot, TypeId = skill }).Entity;
            saved.Points = Math.Max(saved.Points, points);
        }

        void Collect(int type)
        {
            if (!visited.Add(type))
            {
                return;
            }
            var attributes = catalog.FittingAttributes(type);
            foreach (var (skillAttribute, _) in InventoryDefinitions.RequiredSkills)
            {
                int skill = checked((int)attributes.GetValueOrDefault(skillAttribute));
                if (skill != 0)
                {
                    required.Add(skill);
                    Collect(skill);
                }
            }
        }
    }
}
