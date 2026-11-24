using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Exploration;

[Injectable(InjectionType.Singleton)]
internal sealed class IncursionCargoStore(WorldDatabase database, TimeProvider clock) : IIncursionCargo
{
    private sealed record Access(
        Pilot Pilot,
        WorldCombatAnomaly Site,
        IncursionCargoObjective Objective,
        StaticSpaceObject Body,
        bool Source
    );

    private LiveWorld World =>
        LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Incursion cargo requires the resident world.");

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public MissionContainerInventory Read(long pilotId, long containerId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return World.Economy.Inspect(
            world =>
            {
                var access = Open(world, catalog, pilotId, containerId, Now);
                var container = Describe(catalog, access.Body.Id, access.Body.TypeId, pilotId, access.Site.SystemId, 1, singleton: true);
                var contents = access.Source && !access.Site.IncursionCargoIssued ? new[] { Cargo(catalog, access) } : [];
                return new MissionContainerInventory(container, contents)
                {
                    DepositCapacity = access.Source
                        ? null
                        : access.Objective.Quantity * PilotInventoryStore.Volume(catalog, access.Objective.TypeId),
                };
            },
            new(pilots: [pilotId])
        );
    }

    public IReadOnlyList<InventoryChange> Take(long pilotId, long shipId, InventoryMoveRequest request)
    {
        using var world = World.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var access = Open(world, catalog, pilotId, request.SourceId, Now);
        var cargo = Cargo(catalog, access);
        if (
            access.Pilot.ActiveShipId != shipId
            || !access.Source
            || access.Site.IncursionCargoIssued
            || request.ItemIds is not [var item]
            || item != cargo.Id
            || request.Quantity is int moved && moved != cargo.Quantity
        )
        {
            throw new InvalidDataException("This Incursion cargo is no longer available for transfer.");
        }
        world.RequireUncloaked(shipId);
        var hold = ShipHoldRules.Definition(request.FlagId);
        if (hold?.Allows(cargo) != true || request.FlagId == (int)InventoryFlag.FrigateEscapeBay)
        {
            throw new InvalidDataException("The destination hold cannot accept this Incursion cargo.");
        }
        double remaining =
            PilotInventoryStore.HoldCapacity(world, catalog, pilotId, shipId, request.FlagId)
            - InventoryPersistence.UsedVolume(world, catalog, pilotId, shipId, request.FlagId);
        double added = cargo.Quantity * cargo.Volume;
        if (added > remaining + InventoryDefinitions.ResourceTolerance)
        {
            throw new CargoCapacityException(Math.Max(0, remaining), added);
        }
        var result = InventoryPersistence.MergeOutput(world, catalog, pilotId, shipId, request.FlagId, cargo.TypeId, cargo.Quantity);
        access.Site.IncursionCargoIssued = true;
        CombatAnomalyStore.Invalidate(world, access.Site.SystemId);
        world.Commit();
        return [result, new(cargo with { Quantity = 0 }, cargo)];
    }

    public IReadOnlyList<InventoryChange> Deposit(long pilotId, long shipId, long containerId, InventoryMoveRequest request)
    {
        if (
            request.SourceId != shipId
            || request.FlagId is not ((int)InventoryFlag.Cargo or (int)InventoryFlag.None)
            || request.ItemIds.Count == 0
            || request.ItemIds.Count != request.ItemIds.Distinct().Count()
            || request.Quantity is <= 0
            || (request.Quantity.HasValue && request.ItemIds.Count != 1)
        )
        {
            throw new InvalidDataException("Choose cargo from your active ship to deliver.");
        }
        using var world = World.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = Now;
        var access = Open(world, catalog, pilotId, containerId, now);
        if (access.Source || access.Pilot.ActiveShipId != shipId || access.Site.IncursionCargoDelivered)
        {
            throw new InvalidDataException("This Incursion container cannot receive cargo from your ship.");
        }
        world.RequireUncloaked(shipId);
        var items = request.ItemIds.Select(id => world.Set<OwnedItem>().Find(id)).OfType<OwnedItem>().ToArray();
        if (
            items.Length != request.ItemIds.Count
            || items.Any(item =>
                item.OwnerId != pilotId
                || item.LocationId != shipId
                || !ShipHoldRules.IsHold(item.FlagId)
                || item.TypeId != access.Objective.TypeId
                || request.Quantity > item.Quantity
            )
            || items.Sum(item => (long)(request.Quantity ?? item.Quantity)) != access.Objective.Quantity
        )
        {
            throw new InvalidDataException("Deliver the exact item and quantity requested by this Incursion site.");
        }
        List<InventoryChange> changes = [];
        foreach (var item in items)
        {
            var previous = PilotInventoryStore.Read(catalog, item);
            int quantity = item.Quantity - (request.Quantity ?? item.Quantity);
            if (quantity == 0)
            {
                world.Remove(item);
            }
            else
            {
                item.Quantity = quantity;
            }
            changes.Add(new(previous with { Quantity = quantity }, previous));
        }
        CombatAnomalyStore.DeliverIncursionCargo(world, catalog, access.Site, access.Objective, now);
        world.Commit();
        return changes;
    }

    private static Access Open(WorldAction world, CatalogDbContext catalog, long pilotId, long containerId, long now)
    {
        if (
            !IncursionObjectIds.IsCargo(containerId)
            || world.Set<WorldCombatAnomaly>().Find(IncursionObjectIds.Site(containerId))
                is not { CompletedAt: null, IncursionTaleId: long tale } site
            || now < IncursionRules.OpensAt(tale)
            || now >= IncursionRules.EndsAt(tale)
            || IncursionSiteCatalog.Read(catalog, site.DungeonId) is not { Cargo: { } objective } definition
            || world.Pilots.Find(pilotId) is not { ActiveShip.Position: { } position } pilot
            || (position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId) != site.SystemId
        )
        {
            throw new InvalidDataException("This Incursion cargo container is unavailable in your current system.");
        }
        bool source = containerId == IncursionObjectIds.Object(site.Id, IncursionObjectIds.CargoSource);
        var template = source ? objective.Source : objective.Destination;
        if (template is null)
        {
            throw new InvalidDataException("This Incursion cargo container does not exist.");
        }
        int requiredWave = (objective.UnlockWave ?? definition.Encounter.Waves.Length) - 1;
        bool guarded =
            site.Wave < requiredWave
            || world.Filter<WorldNpc>(n => n.EncounterId == site.Id && n.DestroyedAt == null && n.RetiredAt == null).Any();
        var body = objective.Place(site, template);
        MissionContainerRules.Validate(
            new(position.X, position.Y, position.Z),
            catalog.RequireItem(pilot.ActiveShip.TypeId).Radius ?? 0,
            body,
            guarded
        );
        return new(pilot, site, objective, body, source);
    }

    private static InventoryItem Cargo(CatalogDbContext catalog, Access access) =>
        Describe(
            catalog,
            IncursionObjectIds.Object(access.Site.Id, IncursionObjectIds.CargoStack),
            access.Objective.TypeId,
            access.Pilot.Id,
            access.Body.Id,
            access.Objective.Quantity,
            singleton: false
        );

    private static InventoryItem Describe(
        CatalogDbContext catalog,
        long id,
        int typeId,
        long owner,
        long location,
        int quantity,
        bool singleton
    )
    {
        var type = catalog.RequireItem(typeId);
        return new(
            id,
            typeId,
            owner,
            location,
            (int)InventoryFlag.Cargo,
            quantity,
            type.GroupId,
            type.CategoryId,
            PilotInventoryStore.Volume(catalog, typeId, singleton),
            singleton
        );
    }
}
