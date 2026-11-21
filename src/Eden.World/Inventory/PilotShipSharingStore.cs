using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Industry;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Inventory;

[Injectable]
internal sealed partial class PilotShipSharingStore(
    WorldDatabase database,
    ICooperativeFleets fleets,
    IPilotCombatPresence presence,
    Eden.Gameplay.Flight.ICapitalInventoryEvents events,
    TimeProvider clock,
    SqliteWorld lookup
) : IPilotShipSharing, IPilotMobileRefitting, IPilotShipMaintenance
{
    private const int FleetHangarFlag = (int)InventoryFlag.FleetHangar;

    public ShipSharingConfiguration? Configuration(long pilotId, long shipId)
    {
        var live = LiveWorld.Find(database.Path);
        if (live is null)
        {
            return null;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return live.Economy.Inspect(
            world =>
            {
                var actor = world.Pilots.Find(pilotId);
                if (actor is null || actor.ActiveShipId != shipId && Adjacent(world, catalog, actor, shipId) is null)
                {
                    return null;
                }
                return ReadConfiguration(world, shipId);
            },
            new(pilots: [pilotId], ships: [shipId])
        );
    }

    public void Configure(long pilotId, long shipId, ShipSharingChange change)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Ship sharing requires the resident world.");
        using var world = live.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var actor = world.Pilots.Find(pilotId);
        if (
            actor?.ActiveShipId != shipId
            || actor.ActiveShip.OwnerId != pilotId
            || (change.FleetHangarFleet is not null || change.FleetHangarCorporation is not null)
                && Capacity(world, catalog, actor, FleetHangarFlag) <= 0
            || (change.MaintenanceFleet is not null || change.MaintenanceCorporation is not null)
                && Capacity(world, catalog, actor, MaintenanceFlag) <= 0
        )
        {
            throw new InvalidDataException("Configure a supported bay on your own active ship.");
        }
        var settings = world.Set<SavedShipSharing>().Find(shipId) ?? world.Add(new SavedShipSharing { ShipId = shipId }).Entity;
        settings.FleetHangarFleet = change.FleetHangarFleet ?? settings.FleetHangarFleet;
        settings.FleetHangarCorporation = change.FleetHangarCorporation ?? settings.FleetHangarCorporation;
        settings.MaintenanceFleet = change.MaintenanceFleet ?? settings.MaintenanceFleet;
        settings.MaintenanceCorporation = change.MaintenanceCorporation ?? settings.MaintenanceCorporation;
        world.Commit();
    }

    public SharedFleetHangar? FleetHangar(long pilotId, long shipId)
    {
        var live = LiveWorld.Find(database.Path);
        if (live is null)
        {
            return null;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return live.Economy.Inspect(
            world =>
            {
                var actor = world.Pilots.Find(pilotId);
                var host = actor is null ? null : Adjacent(world, catalog, actor, shipId);
                if (
                    host is null
                    || !Permitted(world, actor!, host)
                    || Capacity(world, catalog, host, FleetHangarFlag) is not (> 0 and var capacity)
                )
                {
                    return null;
                }
                var items = world
                    .Filter<OwnedItem>(i => i.LocationId == shipId && i.OwnerId == host.Id && i.FlagId == FleetHangarFlag)
                    .OrderBy(i => i.Id)
                    .AsEnumerable()
                    .Select(i => PilotInventoryStore.Read(catalog, i))
                    .ToArray();
                return new SharedFleetHangar(
                    PilotFleetStore.Read(catalog, host.ActiveShip),
                    items,
                    capacity,
                    InventoryPersistence.UsedVolume(world, catalog, host.Id, shipId, FleetHangarFlag)
                );
            },
            new(pilots: [pilotId], ships: [shipId])
        );
    }

    public IReadOnlyList<InventoryChange>? MoveFleetHangar(long pilotId, long destination, InventoryMoveRequest request)
    {
        var live = LiveWorld.Find(database.Path);
        if (live is null || destination == request.SourceId)
        {
            return null;
        }
        var participants = new TransactionParticipants(pilots: [pilotId], ships: [destination, request.SourceId]);
        using var world = live.Economy.Begin(participants);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Find(pilotId);
        if (pilot?.ActiveShip.Position is null || destination != pilot.ActiveShipId && request.SourceId != pilot.ActiveShipId)
        {
            return null;
        }
        bool deposit = request.SourceId == pilot.ActiveShipId;
        long hostShip = deposit ? destination : request.SourceId;
        if (world.Ships.Find(hostShip) is not { } other || other.OwnerId == pilotId)
        {
            return null;
        }
        var host = Adjacent(world, catalog, pilot, hostShip);
        if (host is null || !Permitted(world, pilot, host))
        {
            throw new InvalidDataException("The shared fleet hangar is unavailable at your current location.");
        }
        var from = deposit ? pilot : host;
        var to = deposit ? host : pilot;
        var hold = ShipHoldRules.Definition(request.FlagId);
        if (
            hold is null
            || hold.RequiresAssembledShip
            || deposit && request.FlagId != FleetHangarFlag
            || request.ItemIds.Count is < 1 or > InventoryStackRules.MaximumMergeOperations
            || request.ItemIds.Distinct().Count() != request.ItemIds.Count
            || request.Quantity is <= 0
            || request.Quantity is not null && request.ItemIds.Count != 1
        )
        {
            throw new InvalidDataException("Choose distinct stacks and a valid fleet hangar transfer.");
        }
        double capacity = Capacity(world, catalog, to, request.FlagId);
        double available = capacity - InventoryPersistence.UsedVolume(world, catalog, to.Id, destination, request.FlagId);
        double required = 0;
        List<(OwnedItem Row, InventoryItem Before, int Quantity)> selected = [];
        foreach (long id in request.ItemIds)
        {
            var row = world.Set<OwnedItem>().Find(id);
            if (
                row is null
                || row.OwnerId != from.Id
                || row.LocationId != request.SourceId
                || row.Online
                || (deposit ? !ShipHoldRules.IsHold(row.FlagId) : row.FlagId != FleetHangarFlag)
                || row.TypeId == CourierRules.PackageType
                || PilotIndustryStore.IsInstalled(world, id)
            )
            {
                throw new InvalidDataException("Choose available items from the authorized hold.");
            }
            var item = PilotInventoryStore.Read(catalog, row);
            int quantity = request.Quantity ?? item.Quantity;
            if (
                quantity > item.Quantity
                || !hold.Allows(item)
                || item.Singleton && Enum.IsDefined((PersonalAssetContainerGroup)item.GroupId)
            )
            {
                throw new InvalidDataException("The selected item or quantity cannot move through the shared hangar.");
            }
            required += hold.RequiredCapacity(item, quantity);
            selected.Add((row, item, quantity));
        }
        if (capacity <= 0 || required > available)
        {
            throw new CargoCapacityException(available, required);
        }
        List<InventoryChange> changes = [];
        foreach (var (row, before, quantity) in selected)
        {
            if (quantity < before.Quantity)
            {
                var split = InventoryStackRules.Split(before, quantity);
                row.Quantity = split.Remainder;
                changes.Add(new(PilotInventoryStore.Read(catalog, row), before));
                var portion = new OwnedItem
                {
                    Id = world.NextItemId(),
                    OwnerId = to.Id,
                    TypeId = row.TypeId,
                    LocationId = destination,
                    FlagId = request.FlagId,
                    Quantity = quantity,
                    CrystalDamage = row.CrystalDamage,
                    VolumeOverride = row.VolumeOverride,
                    BlueprintRuns = row.BlueprintRuns,
                    MaterialEfficiency = row.MaterialEfficiency,
                    TimeEfficiency = row.TimeEfficiency,
                };
                world.Add(portion);
                changes.Add(new(PilotInventoryStore.Read(catalog, portion), null));
            }
            else
            {
                row.OwnerId = to.Id;
                row.LocationId = destination;
                row.FlagId = request.FlagId;
                changes.Add(new(PilotInventoryStore.Read(catalog, row), before));
            }
        }
        world.Commit();
        events.Publish(host.Id, changes);
        return changes;
    }

    private Pilot? Adjacent(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        long shipId,
        double range = ShipSharingRules.TransferRange
    )
    {
        if (
            pilot.ActiveShip.Position is null
            || world.Ships.Find(shipId) is not { Position: not null } hull
            || hull.OwnerId == pilot.Id
            || world.Pilots.Find(hull.OwnerId) is not { } host
            || host.ActiveShipId != shipId
        )
        {
            return null;
        }
        int system = CrimewatchPersistence.System(catalog, pilot.ActiveShip);
        if (
            CrimewatchPersistence.System(catalog, hull) != system
            || presence.IsTravelProtected(system, shipId)
            || presence.IsTravelProtected(system, pilot.ActiveShipId)
            || presence.FindArea(system, shipId) is not { } target
            || presence.FindArea(system, pilot.ActiveShipId) is not { } source
        )
        {
            return null;
        }
        if (
            world.Set<SpatialNavigationState>().Find(shipId) is { CloakModuleId: not 0 }
            || world.Set<SpatialNavigationState>().Find(pilot.ActiveShipId) is { CloakModuleId: not 0 }
        )
        {
            return null;
        }
        double radii = (catalog.RequireItem(hull.TypeId).Radius ?? 0) + (catalog.RequireItem(pilot.ActiveShip.TypeId).Radius ?? 0);
        return source.Position.DistanceTo(target.Position) - radii <= range ? host : null;
    }

    private bool Permitted(WorldAction world, Pilot pilot, Pilot host, bool maintenance = false)
    {
        var settings = ReadConfiguration(world, host.ActiveShipId);
        return (
                (maintenance ? settings.MaintenanceCorporation : settings.FleetHangarCorporation)
                && pilot.CorporationId == host.CorporationId
            )
            || (
                (maintenance ? settings.MaintenanceFleet : settings.FleetHangarFleet)
                && fleets.Identity(pilot.Id) is { } member
                && fleets.Identity(host.Id)?.FleetId == member.FleetId
            );
    }

    private static ShipSharingConfiguration ReadConfiguration(WorldAction world, long shipId) =>
        world.Set<SavedShipSharing>().Find(shipId) is { } row
            ? new(row.FleetHangarFleet, row.FleetHangarCorporation, row.MaintenanceFleet, row.MaintenanceCorporation)
            : new();

    private static double Capacity(WorldAction world, CatalogDbContext catalog, Pilot pilot, int flag) =>
        PilotInventoryStore.HoldCapacity(world, catalog, pilot.Id, pilot.ActiveShipId, flag);
}
