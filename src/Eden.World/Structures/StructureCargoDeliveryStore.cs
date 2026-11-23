using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Data.World.Structures;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Structures;

[Injectable]
internal sealed class StructureCargoDeliveryStore(WorldDatabase database, TimeProvider clock) : IStructureCargoDelivery
{
    public IReadOnlyList<InventoryChange> DropOff(long pilotId, long shipId, long structureId, IReadOnlyList<long> itemIds)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Cargo Deposit requires the resident world.");
        using var world = live.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (
            world.Pilots.Find(pilotId) is not { } pilot
            || pilot.ActiveShipId != shipId
            || pilot.ActiveShip is not { Position: { } position, StoredInId: null } ship
            || ship.OwnerId != pilotId
            || world.Set<ShipLoss>().Find(shipId) is not null
        )
        {
            throw new InvalidDataException("Use Cargo Deposit from your active ship in space.");
        }
        var structure = PilotStructureStore.Require(world, structureId);
        var profile = StructureCatalog.Require(catalog, structure.TypeId);
        if (!profile.Dockable)
        {
            throw new InvalidDataException("This Cargo Deposit service requires a dockable Upwell structure.");
        }
        int system = CrimewatchPersistence.System(catalog, ship);
        if (system != structure.SystemId)
        {
            throw new InvalidDataException("You must be in the same solar system as the structure.");
        }
        if (structure.UnanchorAt <= now)
        {
            throw new InvalidDataException("This structure has finished unanchoring.");
        }
        var battle = world.Set<SavedStructureBattle>().Find(structure.Id);
        if (
            structure.StateEndsAt <= now
            || structure.DeployEndsAt <= now
            || battle?.RepairAt <= now
            || battle is { PausedUntil: > 0 } && battle.PausedUntil <= now
        )
        {
            PilotStructureStore.Advance(world, catalog, now, system);
            structure = PilotStructureStore.Require(world, structureId);
        }
        if (!StructureRules.CanDock(profile, structure.State, 0))
        {
            throw new InvalidDataException("This structure is not operational for Cargo Deposit.");
        }
        world.RequireUncloaked(shipId);
        if (world.Set<SpatialNavigationState>().Find(shipId)?.GateCloakUntil > now)
        {
            throw new InvalidDataException("Decloak before using Cargo Deposit.");
        }
        if (world.Set<SavedCrimewatch>().Find(pilotId) is { } timers)
        {
            if (timers.WeaponsUntil > now)
            {
                throw new InvalidDataException("The weapons timer prevents Cargo Deposit.");
            }
            if (timers.CriminalUntil > now && CrimewatchPersistence.Security(catalog, system) > 0)
            {
                throw new InvalidDataException("The criminal timer prevents Cargo Deposit in empire space.");
            }
        }
        double range = catalog.FittingAttributes(structure.TypeId).GetValueOrDefault(DogmaAttributeId.StructureCargoDeliveryRange);
        double shipRadius = catalog.RequireItem(ship.TypeId).Radius ?? 0;
        double limit = range + profile.Radius + shipRadius;
        var origin = new SpacePosition(position.X, position.Y, position.Z);
        var destination = new SpacePosition(structure.X, structure.Y, structure.Z);
        if (!double.IsFinite(range) || range <= 0 || !double.IsFinite(limit) || !(origin.DistanceTo(destination) <= limit))
        {
            throw new InvalidDataException("Approach within Cargo Deposit range of the structure.");
        }
        List<InventoryChange> changes = [];
        foreach (long id in itemIds.Distinct())
        {
            if (
                id <= 0
                || world.Set<OwnedItem>().Find(id) is not { Quantity: > 0 } item
                || item.OwnerId != pilotId
                || item.LocationId != shipId
                || !StructureCargoDeliveryRules.CanDropOffFrom(item.FlagId)
                || catalog.FittingType(item.TypeId) is null
            )
            {
                continue;
            }
            var before = PilotInventoryStore.Read(catalog, item);
            item.LocationId = structure.Id;
            item.FlagId = (int)InventoryFlag.StructureDeliveries;
            changes.Add(new(before with { LocationId = item.LocationId, FlagId = item.FlagId }, before));
        }
        world.Commit();
        return changes;
    }
}
