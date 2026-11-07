using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Drones;

internal sealed partial class PilotDroneStore
{
    public void Delegate(long pilotId, IReadOnlyList<long> drones, DroneDelegationMode mode, long memberId, long now)
    {
        CheckIds(drones);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = InSpace(world, pilotId);
        int system = CurrentSystem(catalog, pilot);
        var fitting = FittingCalculation.Read(world, catalog, pilot);
        if (
            PilotHealthStore
                .Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(fitting.Graph.Attributes(pilot.ActiveShipId)), now)
                .Remaining.Destroyed
        )
        {
            throw new InvalidDataException("A destroyed ship cannot delegate drones.");
        }
        var owner = DelegationOwner(world, catalog, pilot, system);
        var member = DelegationMember(world, catalog, memberId, system);
        if (member.Available)
        {
            var targetDefense = ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, memberId));
            member = member with { Available = !PilotHealthStore.Read(world, member.ShipId, targetDefense, now).Remaining.Destroyed };
        }
        double range = fitting.Graph.Read(pilotId, DogmaAttributeId.DroneControlDistance);
        var selected = drones.Select(id => world.Set<WorldDrone>().Find(id)).ToArray();
        if (
            selected.Any(d =>
                d is null
                || d.PilotId != pilotId
                || d.ControllerShipId != pilot.ActiveShipId
                || d.SystemId != system
                || owner.Position!.DistanceTo(new(d.X, d.Y, d.Z)) > DroneRules.ShutdownDistance
            )
        )
        {
            throw new InvalidDataException("Assign drones controlled by your current ship on this grid.");
        }
        var controlled = world
            .Set<WorldDrone>()
            .Where(d => d.PilotId == pilotId && d.ControllerShipId == pilot.ActiveShipId && d.SystemId != null)
            .ToArray();
        DroneRules.CheckLaunch(
            0,
            0,
            controlled.Length,
            controlled.Sum(d => DroneCatalog.Profile(catalog, world.Set<OwnedItem>().Find(d.ItemId)!.TypeId).Bandwidth),
            DroneCatalog.ControlLimit(fitting),
            fitting.Graph.Read(pilot.ActiveShipId, DogmaAttributeId.DroneBandwidth)
        );
        int assigned = 0;
        Dictionary<long, DroneDelegateMember?> owners = [];
        foreach (
            var other in world
                .Set<WorldDrone>()
                .Where(d => d.DelegateShipId == member.ShipId && d.DelegationMode != DroneDelegationMode.None)
                .ToArray()
        )
        {
            if (drones.Contains(other.ItemId))
            {
                continue;
            }
            if (!owners.TryGetValue(other.PilotId, out var previousOwner))
            {
                var savedOwner = world
                    .Pilots.Include(p => p.ActiveShip)
                        .ThenInclude(s => s.Position)
                    .SingleOrDefault(p => p.Id == other.PilotId);
                previousOwner = savedOwner is null ? null : DelegationOwner(world, catalog, savedOwner, system);
                owners[other.PilotId] = previousOwner;
            }
            var delegation = DroneDelegationPersistence.Read(other)!;
            if (
                previousOwner is not null
                && previousOwner.ShipId == other.ControllerShipId
                && other.SystemId == system
                && previousOwner.Position is { } savedPosition
                && savedPosition.DistanceTo(new(other.X, other.Y, other.Z)) <= DroneRules.ShutdownDistance
                && DroneDelegationRules.Maintained(delegation, previousOwner, member)
            )
            {
                assigned++;
            }
            else
            {
                CancelDelegation(other, now);
            }
        }
        DroneDelegationRules.CheckAssignment(mode, owner, member, range, assigned, selected.Length);
        long revision = DroneDelegationRules.Trigger(mode, DroneCombatPersistence.Read(world, member.ShipId)).Revision;
        foreach (var entry in selected)
        {
            var drone = entry!;
            PilotSkillReader.CheckRequirements(world, catalog, pilotId, world.Set<OwnedItem>().Find(drone.ItemId)!.TypeId);
            DroneDelegationPersistence.Write(
                drone,
                new(
                    mode,
                    member.PilotId,
                    member.ShipId,
                    owner.Membership!.FleetId,
                    owner.Membership.Version,
                    member.Membership!.Version,
                    revision
                )
            );
            drone.CommandedAt = now;
            drone.Activity = DroneActivity.Idle;
            drone.TargetId = member.ShipId;
            drone.Orbiting = false;
        }
        world.SaveChanges();
        transaction.Commit();
    }

    private DroneDelegateMember DelegationOwner(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, int system)
    {
        var ship = pilot.ActiveShip;
        bool available =
            ship.Position is not null && CurrentSystem(catalog, pilot) == system && !world.Set<ShipLoss>().Any(l => l.ShipId == ship.Id);
        return new(
            pilot.Id,
            ship.Id,
            system,
            ship.Position is { } position ? Position(position) : null,
            catalog.FittingType(ship.TypeId)?.Radius ?? 0,
            available,
            catalog.FittingType(ship.TypeId)?.GroupId == FleetDefinitions.CapsuleGroup,
            fleets.Membership(pilot.Id)
        );
    }

    private DroneDelegateMember DelegationMember(WorldDbContext world, CatalogDbContext catalog, long pilotId, int system)
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == pilotId);
        var online = pilot is null ? null : presence.FindOnline(system, pilot.ActiveShipId);
        bool available =
            pilot?.ActiveShip.Position is not null
            && CurrentSystem(catalog, pilot) == system
            && online is not null
            && !world.Set<ShipLoss>().Any(l => l.ShipId == pilot.ActiveShipId);
        var type = pilot is null ? null : catalog.FittingType(pilot.ActiveShip.TypeId);
        return new(
            pilotId,
            pilot?.ActiveShipId ?? 0,
            system,
            online?.Position,
            type?.Radius ?? 0,
            available,
            type?.GroupId == FleetDefinitions.CapsuleGroup,
            fleets.Membership(pilotId)
        );
    }

    private static void CancelDelegation(WorldDrone drone, long now)
    {
        DroneDelegationPersistence.Write(drone, null);
        drone.Activity = DroneActivity.Idle;
        drone.TargetId = drone.ControllerShipId;
        drone.Orbiting = false;
        drone.CommandedAt = now;
    }

    private DroneDelegateMember? AdvanceDelegation(
        WorldDbContext world,
        CatalogDbContext catalog,
        Pilot pilot,
        WorldDrone drone,
        DroneContact before,
        int system,
        long now
    )
    {
        if (DroneDelegationPersistence.Read(drone) is not { } delegation)
        {
            return null;
        }
        var owner = DelegationOwner(world, catalog, pilot, system);
        var member = DelegationMember(world, catalog, delegation.PilotId, system);
        if (!DroneDelegationRules.Maintained(delegation, owner, member))
        {
            CancelDelegation(drone, now);
            return null;
        }
        var contact = DroneDelegationRules.React(before, DroneCombatPersistence.Read(world, member.ShipId), drone.CommandedAt, now);
        drone.Activity = contact.Activity;
        drone.TargetId = contact.TargetId;
        DroneDelegationPersistence.Write(drone, contact.Delegation);
        return member;
    }
}
