using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Structures;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;

namespace Eden.World.Structures;

internal sealed partial class AssetSafetyStore
{
    internal static SavedAssetSafety? Collect(
        WorldAction world,
        CatalogDbContext catalog,
        SavedStructure structure,
        long owner,
        long now,
        bool eject
    )
    {
        if (world is not EconomyTransaction)
        {
            throw new InvalidOperationException("Asset safety collection requires the resident world.");
        }
        var reserved = world
            .Filter<IndustryJob>(j => j.StationId == structure.Id && j.Status == IndustryJobStatus.Installed)
            .Select(j => j.BlueprintId)
            .ToHashSet();
        var items = world
            .Filter<OwnedItem>(i => i.LocationId == structure.Id && i.OwnerId == owner)
            .AsEnumerable()
            .Where(i =>
                !reserved.Contains(i.Id)
                && (
                    owner != structure.CorporationId
                    || i.FlagId == (int)InventoryFlag.Hangar
                    || CorporationStorageRules.IsDivision(i.FlagId)
                )
            )
            .ToArray();
        var ships = world
            .Filter<Ship>(s => s.OwnerId == owner && s.StructureId == structure.Id && s.StoredInId == null)
            .AsEnumerable()
            .Where(s => s.Position is null && !world.Pilots.Any(p => p.ActiveShipId == s.Id))
            .ToArray();
        if (items.Length == 0 && ships.Length == 0)
        {
            throw new InvalidDataException("There are no available assets to move to safety.");
        }
        long location;
        SavedAssetSafety? wrap = null;
        if (eject)
        {
            location = world.Next("WorldStructureLoot", AssetSafetyRules.FirstLootContainerId - 1);
            if (location >= AssetSafetyRules.LootContainerLimit)
            {
                throw new InvalidDataException("No cargo container identities remain.");
            }
            world.Add(
                new SavedStructureLoot
                {
                    Id = location,
                    OwnerId = owner,
                    CorporationId = structure.CorporationId,
                    StructureId = structure.Id,
                    SystemId = structure.SystemId,
                    Name = structure.Name + " assets",
                    X = structure.X + StructureCatalog.Require(catalog, structure.TypeId).Radius,
                    Y = structure.Y,
                    Z = structure.Z,
                    ExpiresAt = checked(now + JetcanRules.Lifetime),
                }
            );
        }
        else
        {
            location = InventoryPersistence.NextId(world);
            wrap = new()
            {
                Id = location,
                OwnerId = owner,
                StructureId = structure.Id,
                SystemId = structure.SystemId,
                Name = structure.Name + " (" + catalog.RequireSystem(structure.SystemId).Name + ")",
                CreatedAt = now,
                NearestStationId = NearestStation(catalog, structure.SystemId),
            };
            world.Add(wrap);
        }
        foreach (var item in items)
        {
            item.LocationId = location;
            if (eject)
            {
                item.FlagId = (int)InventoryFlag.Cargo;
            }
        }
        foreach (var ship in ships)
        {
            ship.StructureId = null;
            ship.StoredInId = location;
            ship.StorageFlagId = eject ? (int)InventoryFlag.Cargo : ship.StorageFlagId;
        }
        return wrap;
    }

    public AssetSafetyWrap Deliver(long pilotId, long wrapId, int systemId, long destinationId) =>
        Execute(
            pilotId,
            (world, catalog, now) =>
            {
                var wrap = Authorize(world, pilotId, wrapId);
                if (wrap.SystemId != systemId || wrap.DeliveredAt is not null || now - wrap.CreatedAt < AssetSafetyRules.ManualDelay)
                {
                    throw new InvalidDataException("The wrap is not ready for this delivery or has already been delivered.");
                }
                int destinationSystem;
                if (destinationId is > 0 and <= int.MaxValue && catalog.FindStation((int)destinationId) is { } station)
                {
                    destinationSystem = station.SolarSystemId;
                }
                else if (
                    world.Set<SavedStructure>().Find(destinationId) is { } structure
                    && StructureRules.CanDock(StructureCatalog.Require(catalog, structure.TypeId), structure.State, 0)
                    && PilotStructureStore.Access(world, structure, pilotId)
                )
                {
                    destinationSystem = structure.SystemId;
                }
                else
                {
                    throw new InvalidDataException("The destination is unavailable or does not grant docking access.");
                }
                if (
                    destinationSystem != systemId
                    && (now - wrap.CreatedAt < AssetSafetyRules.AutomaticDelay || destinationId != wrap.NearestStationId)
                )
                {
                    throw new InvalidDataException("Early delivery must stay in the original solar system.");
                }
                Deliver(world, wrap, destinationId, now);
                return Snapshot(catalog, wrap);
            },
            systemId
        );

    private static void Advance(WorldAction world, CatalogDbContext catalog, long owner, long now)
    {
        foreach (
            var wrap in world
                .Filter<SavedAssetSafety>(w => w.OwnerId == owner && w.DeliveredAt == null)
                .AsEnumerable()
                .Where(w => now - w.CreatedAt >= AssetSafetyRules.AutomaticDelay)
        )
        {
            _ = catalog.RequireStation(wrap.NearestStationId);
            Deliver(world, wrap, wrap.NearestStationId, checked(wrap.CreatedAt + AssetSafetyRules.AutomaticDelay));
        }
    }

    private static void Deliver(WorldAction world, SavedAssetSafety wrap, long destination, long now)
    {
        if (wrap.DeliveredAt is not null || world.Set<OwnedItem>().Find(wrap.Id) is not null)
        {
            throw new InvalidDataException("The wrap has already been delivered.");
        }
        world.Add(
            new OwnedItem
            {
                Id = wrap.Id,
                TypeId = AssetSafetyRules.WrapTypeId,
                OwnerId = wrap.OwnerId,
                LocationId = destination,
                FlagId = world.Pilots.Find(wrap.OwnerId) is not null
                    ? (int)InventoryFlag.Hangar
                    : CorporationStorageRules.MarketDeliveriesFlag,
                Quantity = 1,
                Singleton = true,
            }
        );
        wrap.DestinationId = destination;
        wrap.DeliveredAt = now;
    }

    public AssetSafetyContents Inventory(long pilotId, long wrapId) =>
        Execute(
            pilotId,
            (world, catalog, now) =>
            {
                var ancestor = Ancestor(world, wrapId) ?? throw new InvalidDataException("The safety inventory is unavailable.");
                var wrap = Authorize(world, pilotId, ancestor.Id);
                Advance(world, catalog, wrap.OwnerId, now);
                var pilot = world.Pilots.Find(pilotId)!;
                if (wrap.DestinationId != pilot.ActiveShip.DockedLocationId || pilot.ActiveShip.Position is not null)
                {
                    throw new InvalidDataException("Dock at the delivered wrap's location before accessing it.");
                }
                var root = world.Set<OwnedItem>().Find(wrapId) ?? throw new InvalidDataException("The safety inventory is unavailable.");
                if (
                    wrapId != wrap.Id
                    && (
                        root.OwnerId != wrap.OwnerId
                        || !Enum.IsDefined((PersonalAssetContainerGroup)catalog.RequireItem(root.TypeId).GroupId)
                    )
                )
                {
                    throw new InvalidDataException("Only owned cargo containers inside this wrap may be opened.");
                }
                var items = world
                    .Filter<OwnedItem>(i => i.LocationId == wrapId && i.OwnerId == wrap.OwnerId)
                    .AsEnumerable()
                    .Select(i => PilotInventoryStore.Read(catalog, i))
                    .Concat(
                        world
                            .Filter<Ship>(s => s.StoredInId == wrapId && s.OwnerId == wrap.OwnerId)
                            .AsEnumerable()
                            .Select(s => PilotFleetStore.Read(catalog, s))
                    )
                    .OrderBy(i => i.Id)
                    .ToArray();
                return new AssetSafetyContents(PilotInventoryStore.Read(catalog, root), items);
            }
        );
}
