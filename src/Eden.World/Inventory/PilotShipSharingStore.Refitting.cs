using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Inventory;

internal sealed partial class PilotShipSharingStore
{
    public IReadOnlyList<InventoryChange> Fit(long pilotId, long shipId, InventoryMoveRequest request)
    {
        using var world = BeginRefit(pilotId, shipId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (request.SourceId != shipId)
        {
            throw new InvalidDataException("Fit equipment from your active ship's cargo while using a mobile fitting service.");
        }
        RequireRefit(world, catalog, pilotId, shipId, request.ItemIds, now);
        var changes = PilotFittingStore.Fit(world, catalog, pilotId, request.ItemIds, shipId, request.FlagId, now, mobileRefit: true);
        world.Commit();
        return changes;
    }

    public IReadOnlyList<InventoryChange>? Unfit(long pilotId, long shipId, InventoryMoveRequest request)
    {
        if (request.SourceId != shipId || request.FlagId != (int)InventoryFlag.Cargo)
        {
            return null;
        }
        using var world = BeginRefit(pilotId, shipId);
        var contents = request.ItemIds.Select(id => world.Set<OwnedItem>().Find(id)).ToArray();
        bool Fitted(OwnedItem? item) => item is not null && InventoryDefinitions.IsFittedSlot(item.FlagId);
        if (!contents.Any(Fitted))
        {
            return null;
        }
        if (contents.Any(item => !Fitted(item) || InventoryDefinitions.IsRigSlot(item!.FlagId)))
        {
            throw new InvalidDataException("Move only high, medium or low power modules into cargo when refitting in space.");
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        RequireRefit(world, catalog, pilotId, shipId, request.ItemIds, now);
        var changes = PilotInventoryStore.Move(world, catalog, pilotId, shipId, request, now, mobileRefit: true);
        world.Commit();
        return changes;
    }

    public IReadOnlyList<InventoryChange> SetOnline(long pilotId, long shipId, long moduleId, bool online)
    {
        using var world = BeginRefit(pilotId, shipId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var pilot = RequireRefitShip(world, catalog, pilotId, shipId, now);
        RequireIdleModules(world, shipId, [moduleId], now);
        var (system, position) = RefitPosition(catalog, pilot);
        bool pay = online && (WeaponsTimerActive(world, pilotId, now) || !HasFittingService(world, catalog, pilot, system, position));
        var mode = pay ? ModuleOnlineMode.InSpaceWithCapacitorCost : ModuleOnlineMode.InSpaceWithoutCapacitorCost;
        var changes = PilotFittingStore.SetOnline(world, catalog, pilotId, shipId, moduleId, online, now, mode);
        world.Commit();
        return changes;
    }

    private EconomyTransaction BeginRefit(long pilotId, long shipId) =>
        (LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Mobile fitting requires the resident world.")).Economy.Begin(
            new TransactionParticipants(pilots: [pilotId], ships: [shipId])
        );

    private void RequireRefit(WorldAction world, CatalogDbContext catalog, long pilotId, long shipId, IReadOnlyList<long> items, long now)
    {
        var pilot = RequireRefitShip(world, catalog, pilotId, shipId, now);
        if (WeaponsTimerActive(world, pilotId, now))
        {
            throw new InvalidDataException("The weapons timer prevents refitting in space.");
        }
        RequireIdleModules(world, shipId, items, now);
        var (system, position) = RefitPosition(catalog, pilot);
        if (!HasFittingService(world, catalog, pilot, system, position))
        {
            throw new InvalidDataException("Approach a fleet or corporation ship that provides a mobile fitting service.");
        }
    }

    private static Pilot RequireRefitShip(WorldAction world, CatalogDbContext catalog, long pilotId, long shipId, long now)
    {
        var pilot = world.Pilots.Find(pilotId);
        if (pilot?.ActiveShipId != shipId || pilot.ActiveShip.Position is null || pilot.ActiveShip.OwnerId != pilotId)
        {
            throw new InvalidDataException("Change your fitting only on your active ship in space.");
        }
        world.RequireUncloaked(shipId);
        Eden.World.Flight.PilotCapitalTravelStore.RequireMobile(world, catalog, pilotId, shipId, now);
        return pilot;
    }

    private static bool WeaponsTimerActive(WorldAction world, long pilotId, long now) =>
        world.Set<SavedCrimewatch>().Find(pilotId)?.WeaponsUntil > now;

    private static void RequireIdleModules(WorldAction world, long shipId, IReadOnlyList<long> items, long now)
    {
        if (
            items.Any(id =>
                world.Set<ActiveFittedModule>().Find(id) is { } active
                && active.ShipId == shipId
                && (active.ExpiresAt == 0 || active.ExpiresAt > now)
            )
        )
        {
            throw new InvalidDataException("Stop the active module before changing the fit.");
        }
    }

    private (int System, SpacePosition Position) RefitPosition(CatalogDbContext catalog, Pilot pilot)
    {
        long shipId = pilot.ActiveShipId;
        int system = CrimewatchPersistence.System(catalog, pilot.ActiveShip);
        if (presence.IsTravelProtected(system, shipId) || presence.FindArea(system, shipId) is not { } source)
        {
            throw new InvalidDataException("Wait until your ship can change its fitting.");
        }
        return (system, source.Position);
    }

    private bool HasFittingService(WorldAction world, CatalogDbContext catalog, Pilot pilot, int system, SpacePosition position)
    {
        bool MaintenanceBay(Pilot candidate) =>
            catalog.FittingAttributes(candidate.ActiveShip.TypeId).GetValueOrDefault(DogmaAttributeId.ShipMaintenanceBayCapacity) > 0;
        if (MaintenanceBay(pilot))
        {
            return true;
        }
        var fleet = fleets.Identity(pilot.Id)?.FleetId;
        foreach (long other in presence.NearbyShips(system, position))
        {
            var host = Adjacent(world, catalog, pilot, other, ShipSharingRules.FittingRange);
            if (
                host is not null
                && MaintenanceBay(host)
                && (host.CorporationId == pilot.CorporationId || fleet is not null && fleets.Identity(host.Id)?.FleetId == fleet)
            )
            {
                return true;
            }
        }
        return false;
    }
}
