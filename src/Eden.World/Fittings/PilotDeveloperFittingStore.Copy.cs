using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Inventory;

namespace Eden.World.Fittings;

internal sealed partial class PilotDeveloperFittingStore
{
    public DeveloperShipCopy CopyDocked(long pilot, long ship)
    {
        using var world = Resident.Economy.Begin(new(pilots: [pilot], ships: [ship]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var source = world.Pilots.Find(pilot)?.ActiveShip;
        if (
            source is null
            || source.Id != ship
            || source.OwnerId != pilot
            || source.Position is not null
            || source.StoredInId.HasValue
            || catalog.RequireItem(source.TypeId).GroupId == FleetDefinitions.CapsuleGroup
        )
        {
            throw new InvalidDataException("Dock in your own active ship before copying it.");
        }

        Dictionary<long, long> identities = [];
        Queue<long> containers = new();
        List<Ship> hulls = [];
        List<InventoryChange> changes = [];
        long copy = CopyHull(source, null);
        while (containers.TryDequeue(out long original))
        {
            long parent = identities[original];
            foreach (var row in world.Filter<OwnedItem>(item => item.LocationId == original).ToArray())
            {
                if (
                    row.OwnerId != pilot
                    || row.FlagId == (int)InventoryFlag.HiddenModifiers
                    || row.TypeId == CourierRules.PackageType
                    || catalog.RequireItem(row.TypeId).CategoryId == (int)InventoryCategory.Blueprint
                )
                {
                    throw new InvalidDataException("Remove unsupported or externally owned items before copying this ship.");
                }
                long id = Allocate(row.Id);
                var item = new OwnedItem
                {
                    Id = id,
                    OwnerId = pilot,
                    TypeId = row.TypeId,
                    LocationId = parent,
                    FlagId = row.FlagId,
                    Quantity = row.Quantity,
                    BlueprintRuns = row.BlueprintRuns,
                    MaterialEfficiency = row.MaterialEfficiency,
                    TimeEfficiency = row.TimeEfficiency,
                    Singleton = row.Singleton,
                    Online = row.Online,
                    CrystalDamage = row.CrystalDamage,
                    VolumeOverride = row.VolumeOverride,
                };
                world.Add(item);
                if (world.Set<SavedModuleCondition>().Find(row.Id) is { } condition)
                {
                    world.Add(new SavedModuleCondition { ModuleId = id, Damage = condition.Damage });
                }
                if (world.Set<SavedMutatedItem>().Find(row.Id) is { } mutation)
                {
                    world.Add(new SavedMutatedItem { ItemId = id, Payload = mutation.Payload });
                }
                changes.Add(new(PilotInventoryStore.Read(catalog, item), null));
            }
            foreach (var hull in world.Filter<Ship>(hull => hull.StoredInId == original).ToArray())
            {
                CopyHull(hull, parent);
            }
        }
        foreach (var hull in hulls)
        {
            foreach (var link in world.Filter<SavedWeaponLink>(link => link.ShipId == hull.Id).ToArray())
            {
                world.Add(
                    new SavedWeaponLink
                    {
                        ShipId = identities[hull.Id],
                        MasterId = identities[link.MasterId],
                        SlaveId = identities[link.SlaveId],
                    }
                );
            }
        }
        world.Commit();
        return new(copy, source.Name, changes);

        long Allocate(long original)
        {
            long id = world.NextItemId();
            if (!identities.TryAdd(original, id))
            {
                throw new InvalidDataException("This ship contains overlapping inventory locations.");
            }
            containers.Enqueue(original);
            return id;
        }

        long CopyHull(Ship original, long? parent)
        {
            if (original.OwnerId != pilot || original.Position is not null)
            {
                throw new InvalidDataException("Remove externally owned ships before copying this ship.");
            }
            long id = Allocate(original.Id);
            Ship hull = new()
            {
                Id = id,
                OwnerId = pilot,
                TypeId = original.TypeId,
                Name = original.Name,
                StationId = source.StationId,
                StructureId = source.StructureId,
                StoredInId = parent,
                StorageFlagId = original.StorageFlagId,
            };
            world.Ships.Add(hull);
            hulls.Add(original);
            if (world.Set<ShipHealthState>().Find(original.Id) is { } health)
            {
                world.Add(
                    new ShipHealthState
                    {
                        ShipId = id,
                        ShieldCharge = health.ShieldCharge,
                        ArmorDamage = health.ArmorDamage,
                        StructureDamage = health.StructureDamage,
                        UpdatedAt = health.UpdatedAt,
                    }
                );
            }
            if (world.Set<ShipEnergy>().Find(original.Id) is { } energy)
            {
                world.Add(
                    new ShipEnergy
                    {
                        ShipId = id,
                        Charge = energy.Charge,
                        UpdatedAt = energy.UpdatedAt,
                    }
                );
            }
            changes.Add(new(PilotFleetStore.Read(catalog, hull), null));
            return id;
        }
    }
}
