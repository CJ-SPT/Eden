using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Combat;

internal sealed partial class AreaWeaponStore
{
    internal static bool DoomsdayLocked(WorldAction world, long ship, long now, bool movement) =>
        world is EconomyTransaction
        && world
            .Filter<SavedAreaWeapon>(row => row.SourceShipId == ship)
            .AsEnumerable()
            .Any(row =>
            {
                var flight = row.Flight();
                return flight.Module.Doomsday is { } charge
                    && now
                        < AreaWeaponRules.Deadline(
                            flight.StartedAt,
                            movement ? charge.MovementLockMilliseconds : charge.DriveLockMilliseconds
                        );
            });

    internal static void RequireDoomsdayCooldownComplete(WorldAction world, long ship, long now)
    {
        if (DoomsdayLocked(world, ship, now, movement: false))
        {
            throw new InvalidDataException("The doomsday cooldown prevents docking, cloaking and jump drive travel.");
        }
    }

    private Target? DoomsdayTarget(WorldAction world, CatalogDbContext catalog, Pilot source, long targetId, int system, long now)
    {
        if (
            !targetLocks.Contains(source.ActiveShipId, targetId)
            || world.Ships.Find(targetId) is not { Position: { } position } victimShip
            || world.Pilots.Find(victimShip.OwnerId) is not { } victim
            || victim.Id == source.Id
            || victim.ActiveShipId != targetId
            || CrimewatchPersistence.System(catalog, victimShip) != system
            || world.Set<ShipLoss>().Find(targetId) is not null
            || world.Set<SpatialNavigationState>().Find(targetId) is { CloakModuleId: not 0 }
            || world.TetherProtected(targetId, now)
            || presence.IsAreaProtected(system, targetId)
            || source.ActiveShip.Position is not { } sourcePosition
        )
        {
            return null;
        }
        var hull = catalog.RequireItem(victimShip.TypeId);
        if (catalog.FittingAttributes(hull.Id).GetValueOrDefault(DogmaAttributeId.IsCapitalSize) <= 0)
        {
            return null;
        }
        var sourcePoint =
            presence.FindArea(system, source.ActiveShipId)?.Position ?? new(sourcePosition.X, sourcePosition.Y, sourcePosition.Z);
        var point = presence.FindArea(system, targetId)?.Position ?? new(position.X, position.Y, position.Z);
        double distance = sourcePoint.DistanceTo(point) - (catalog.RequireItem(source.ActiveShip.TypeId).Radius ?? 0) - (hull.Radius ?? 0);
        if (distance > PilotFittingStore.ShipAttributes(world, catalog, source).GetValueOrDefault(DogmaAttributeId.MaxTargetRange))
        {
            return null;
        }
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, victim);
        return PilotHealthStore.Read(world, targetId, ShipHealth.DefenseFrom(attributes), now).Remaining.Destroyed
            ? null
            : new(targetId, hull.Id, point, hull.Radius ?? 0, attributes, Pilot: victim);
    }

    private void SettleDoomsday(
        WorldAction world,
        CatalogDbContext catalog,
        SavedAreaWeapon row,
        AreaWeaponFlight flight,
        long now,
        List<PilotDamage> notifications
    )
    {
        if (flight.Active || flight.Module.Doomsday is not { } charge)
        {
            world.Remove(row);
            return;
        }
        if (
            world.Pilots.Find(row.PilotId) is { } source
            && source.ActiveShipId == row.SourceShipId
            && source.ActiveShip.Position is not null
            && CrimewatchPersistence.System(catalog, source.ActiveShip) == row.SystemId
            && world.Set<ShipLoss>().Find(row.SourceShipId) is null
            && world.Set<OwnedItem>().Find(row.ModuleId) is { Online: true, Singleton: true } weapon
            && weapon.LocationId == row.SourceShipId
            && weapon.OwnerId == row.PilotId
            && weapon.TypeId == flight.Module.TypeId
            && DoomsdayTarget(world, catalog, source, flight.TargetId, row.SystemId, now) is { Pilot: { } victim } target
        )
        {
            HitPilot(world, catalog, row, source, target, victim, flight.Module.TypeId, charge.Damage, now, notifications);
        }
        long lockUntil = AreaWeaponRules.Deadline(flight.StartedAt, charge.DriveLockMilliseconds);
        if (now >= lockUntil)
        {
            world.Remove(row);
        }
        else
        {
            row.Save(flight with { Active = true, ResolvesAt = lockUntil });
        }
    }
}
