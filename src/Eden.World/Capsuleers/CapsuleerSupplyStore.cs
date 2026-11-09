using Eden.Data.Catalog;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.World.Catalog;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal static class CapsuleerSupplyStore
{
    private const int SpareMagazines = 5;
    private static readonly CatalogDefinition<DroneProfile[]> s_drones = new("capsuleer-starter-drones", LoadDrones);

    internal static void Ensure(WorldDbContext world, CatalogDbContext catalog, long pilotId, long founderShip)
    {
        if (world.Set<SavedCapsuleerSupplies>().Find(pilotId) is not null)
        {
            return;
        }
        var pilot = world.Pilots.Include(value => value.ActiveShip).ThenInclude(ship => ship.Position).Single(p => p.Id == pilotId);
        var ship = pilot.ActiveShip;
        if (
            ship.Id != founderShip
            || ship.OwnerId != pilotId
            || ship.Position is not null
            || world.Set<ShipLoss>().Any(loss => loss.ShipId == founderShip)
        )
        {
            return;
        }
        // The owning provisioning/departure transaction commits inventory and this permanent marker together.
        world.Add(new SavedCapsuleerSupplies { PilotId = pilotId, ShipId = founderShip });
        var inventory = world.Set<OwnedItem>().Where(item => item.OwnerId == pilotId && item.LocationId == ship.Id).ToArray();
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
        double free = Math.Max(
            0,
            attributes.GetValueOrDefault(DogmaAttributeId.CargoCapacity)
                - InventoryPersistence.UsedVolume(world, catalog, pilotId, ship.Id, (int)InventoryFlag.Cargo)
        );
        foreach (
            var module in inventory
                .Where(item => item.Singleton && item.Online && InventoryDefinitions.IsFittedSlot(item.FlagId))
                .OrderBy(item => item.FlagId)
        )
        {
            var effect = FittingCatalog.DefaultEffect(catalog, module.TypeId);
            if (
                effect
                is not (
                    DogmaEffect.LaserFired
                    or DogmaEffect.ProjectileFired
                    or DogmaEffect.UseMissiles
                    or DogmaEffect.AutoTargetingMissile
                    or DogmaEffect.CapacitorBoosting
                    or DogmaEffect.AncillaryShieldBoosting
                    or DogmaEffect.AncillaryArmorRepair
                )
            )
            {
                continue;
            }
            var charge = world.Set<OwnedItem>().SingleOrDefault(item => item.LocationId == module.Id);
            if (charge is null || SupportModuleDefinitions.IsScript(catalog.RequireItem(charge.TypeId).GroupId))
            {
                continue;
            }
            if (
                AmmunitionDefinitions.IsCrystalGroup(catalog.RequireItem(charge.TypeId).GroupId)
                && catalog.FittingAttributes(charge.TypeId).GetValueOrDefault(DogmaAttributeId.CrystalsGetDamaged) == 0
            )
            {
                continue;
            }
            double volume = PilotInventoryStore.Volume(catalog, charge.TypeId);
            double capacity = catalog.RequireItem(module.TypeId).Capacity ?? 0;
            if (volume <= 0 || capacity <= 0)
            {
                continue;
            }
            int quantity = (int)Math.Min(int.MaxValue, Math.Min(Math.Floor(free / volume), Math.Floor(capacity / volume) * SpareMagazines));
            if (quantity > 0)
            {
                Add(charge.TypeId, quantity, InventoryFlag.Cargo);
                free -= quantity * volume;
            }
        }
        if (
            DroneRules.SupportsHull(ship.TypeId)
            && attributes.GetValueOrDefault(DogmaAttributeId.DroneCapacity) > 0
            && !inventory.Any(item => item.FlagId == (int)InventoryFlag.DroneBay)
        )
        {
            var drones = catalog.Definition(s_drones, 0);
            if (drones.Length > 0)
            {
                GrantDroneSkills(world, catalog, pilotId);
                var fit = FittingCalculation.Read(world, catalog, pilot);
                int maximum = DroneCatalog.ControlLimit(fit);
                double bandwidth = fit.Graph.Read(ship.Id, DogmaAttributeId.DroneBandwidth);
                double bay = fit.Graph.Read(ship.Id, DogmaAttributeId.DroneCapacity);
                var candidates = drones
                    .Select(profile => new
                    {
                        Profile = profile,
                        Count = Math.Min(
                            maximum,
                            (int)Math.Min(Math.Floor(bandwidth / profile.Bandwidth), Math.Floor(bay / profile.Volume))
                        ),
                    })
                    .Where(value => value.Count > 0)
                    .ToArray();
                // Prefer a full flight, then the largest mobile class, with a reproducible catalog tie-break.
                var selected = candidates
                    .OrderByDescending(value => value.Count)
                    .ThenByDescending(value => value.Profile.Bandwidth)
                    .ThenBy(value => value.Profile.TypeId)
                    .FirstOrDefault();
                if (selected is not null)
                {
                    CapsuleerCommunityStore.GrantRequirements(world, catalog, pilotId, selected.Profile.TypeId, []);
                    Add(selected.Profile.TypeId, selected.Count, InventoryFlag.DroneBay);
                }
            }
        }
        world.SaveChanges();

        void Add(int type, int quantity, InventoryFlag flag)
        {
            world.Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = pilotId,
                    LocationId = ship.Id,
                    TypeId = type,
                    Quantity = quantity,
                    FlagId = (int)flag,
                }
            );
            world.SaveChanges();
        }
    }

    private static void GrantDroneSkills(WorldDbContext world, CatalogDbContext catalog, long pilot)
    {
        foreach (
            var skill in new[]
            {
                DroneSkill.Drones,
                DroneSkill.DroneAvionics,
                DroneSkill.DroneInterfacing,
                DroneSkill.DroneNavigation,
                DroneSkill.DroneDurability,
                DroneSkill.DroneSharpshooting,
            }
        )
        {
            double rank = catalog.FittingAttributes((int)skill).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant);
            if (rank <= 0)
            {
                continue;
            }
            CapsuleerCommunityStore.GrantRequirements(world, catalog, pilot, (int)skill, []);
            long points = TrainedSkill.PointsForLevel(rank, TrafficRules.MaximumSkillLevel);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPilotSkill(PilotId,TypeId,Points) VALUES({pilot},{(int)skill},{points})
                ON CONFLICT(PilotId,TypeId) DO UPDATE SET Points=MAX(Points,excluded.Points)
                """
            );
        }
    }

    private static DroneProfile[] LoadDrones(CatalogDbContext catalog, int unused)
    {
        List<DroneProfile> profiles = [];
        foreach (var pair in catalog.Records(CatalogData.Client.Types).Where(pair => pair.Value.GroupID == DroneRules.CombatDroneGroup))
        {
            int type = checked((int)pair.Key);
            if (catalog.FittingAttributes(type).GetValueOrDefault(DogmaAttributeId.TechLevel) != 1)
            {
                continue;
            }
            try
            {
                var profile = DroneCatalog.Profile(catalog, type);
                if (profile.Kind is CombatDroneKind.Light or CombatDroneKind.Medium or CombatDroneKind.Heavy)
                {
                    profiles.Add(profile);
                }
            }
            catch (InvalidDataException)
            {
                // Unsupported catalog families do not become starter equipment.
            }
        }
        return profiles.ToArray();
    }
}
