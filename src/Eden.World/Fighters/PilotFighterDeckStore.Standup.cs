using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Structures;

namespace Eden.World.Fighters;

internal sealed partial class PilotFighterDeckStore
{
    private sealed record FighterStockDeck(long Owner, IReadOnlyDictionary<DogmaAttributeId, double> Attributes, bool Standup)
    {
        internal FighterClass? Class(IReadOnlyDictionary<DogmaAttributeId, double> fighter) =>
            Standup ? FighterDeckRules.StandupClass(fighter) : FighterDeckRules.Class(fighter);
    }

    private FighterDeckSnapshot ReadStandupDeck(WorldAction world, long pilotId, long structureId)
    {
        if (world.Set<SavedStructure>().Find(structureId) is not { } structure)
        {
            return new([], []);
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var ship = world.Pilots.Find(pilotId)?.ActiveShip;
        if (
            ship?.StructureId != structureId
            || ship.Position is not null
            || catalog.FittingAttributes(structure.TypeId).GetValueOrDefault(DogmaAttributeId.FighterTubes) <= 0
            || !PilotStructureStore.CanControl(world, catalog, structure, pilotId)
        )
        {
            return new([], []);
        }
        return new(
            TubeItems(world, structure.CorporationId, structureId).OrderBy(item => item.FlagId).Select(Tube).ToArray(),
            Deployed(world, pilotId, structureId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()).ToArray()
        );
    }

    private static FighterStockDeck RequireStockDeck(WorldAction world, CatalogDbContext catalog, long pilotId, long hostId, int flag)
    {
        if (world.Pilots.Find(pilotId)?.ActiveShipId == hostId)
        {
            var pilot = RequireDeck(world, catalog, pilotId, hostId, flag);
            return new(pilotId, PilotFittingStore.ShipAttributes(world, catalog, pilot), false);
        }
        var structure = PilotStructureStore.Require(world, hostId);
        var controller = PilotStructureStore.Manage(world, structure, pilotId);
        var attributes = catalog.FittingAttributes(structure.TypeId);
        if (
            controller.ActiveShip.StructureId != hostId
            || controller.ActiveShip.Position is not null
            || !StructureRules.CanDock(StructureCatalog.Require(catalog, structure.TypeId), structure.State, 0)
            || !FighterDeckRules.IsTube(flag)
            || flag - FighterDeckRules.FirstTube >= attributes.GetValueOrDefault(DogmaAttributeId.FighterTubes)
        )
        {
            throw new InvalidDataException("Choose an available fighter tube on your docked Upwell structure.");
        }
        return new(structure.CorporationId, attributes, true);
    }

    public StandupFighterBay Bay(long pilotId, long structureId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return Live.Economy.Inspect(
            world =>
            {
                var host = RequireStockDeck(world, catalog, pilotId, structureId, FighterDeckRules.FirstTube);
                return ReadStandupBay(world, catalog, host, structureId);
            },
            new(pilots: [pilotId], ships: [structureId])
        );
    }

    public IReadOnlyList<InventoryChange> MoveStandupFighters(long pilotId, long structureId, long itemId, int? quantity, bool deposit)
    {
        using var world = Live.Economy.Begin(new(pilots: [pilotId], ships: [structureId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var host = RequireStockDeck(world, catalog, pilotId, structureId, FighterDeckRules.FirstTube);
        const int bayFlag = (int)InventoryFlag.FighterBay;
        const int hangarFlag = (int)InventoryFlag.Hangar;
        var item =
            InventoryPersistence.OwnedStack(world, deposit ? pilotId : host.Owner, itemId)
            ?? throw new InvalidDataException("Choose a fighter stack in the local hangar or bay.");
        int count = quantity ?? item.Quantity;
        if (
            !host.Standup
            || item.LocationId != structureId
            || item.FlagId != (deposit ? hangarFlag : bayFlag)
            || item.Singleton
            || count <= 0
            || count > item.Quantity
            || catalog.Read(CatalogData.Client.Types, (long)item.TypeId)?.Published != 1
            || FighterDeckRules.StandupClass(catalog.FittingAttributes(item.TypeId)) is null
        )
        {
            throw new InvalidDataException("Move published Standup fighter stacks between this hangar and fighter bay.");
        }
        var previous = PilotInventoryStore.Read(catalog, item);
        if (deposit)
        {
            var capacity = ReadStandupBay(world, catalog, host, structureId);
            double required = ShipHoldRules.Definition(bayFlag)!.RequiredCapacity(previous, count);
            if (capacity.Capacity - capacity.Used < required)
            {
                throw new CargoCapacityException(capacity.Capacity - capacity.Used, required);
            }
        }
        long owner = deposit ? host.Owner : pilotId;
        int flag = deposit ? bayFlag : hangarFlag;
        if (count == item.Quantity)
        {
            item.OwnerId = owner;
            item.FlagId = flag;
            var updated = PilotInventoryStore.Read(catalog, item);
            world.Commit();
            return [new(updated, previous)];
        }
        item.Quantity -= count;
        var split = world
            .Add(
                new OwnedItem
                {
                    Id = world.NextItemId(),
                    OwnerId = owner,
                    LocationId = structureId,
                    TypeId = item.TypeId,
                    FlagId = flag,
                    Quantity = count,
                }
            )
            .Entity;
        var changes = new InventoryChange[]
        {
            new(PilotInventoryStore.Read(catalog, item), previous),
            new(PilotInventoryStore.Read(catalog, split), null),
        };
        world.Commit();
        return changes;
    }

    private static StandupFighterBay ReadStandupBay(WorldAction world, CatalogDbContext catalog, FighterStockDeck host, long structureId) =>
        new(
            host.Attributes.GetValueOrDefault(DogmaAttributeId.FighterCapacity),
            InventoryPersistence.UsedVolume(world, catalog, host.Owner, structureId, (int)InventoryFlag.FighterBay)
        );
}
