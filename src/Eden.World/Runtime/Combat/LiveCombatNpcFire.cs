using Eden.Data.World.Drones;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.World.Drones;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    private ResidentMap<long, long>? _droneControllers;

    private sealed record DroneController(long ControllerShipId, long PilotId);

    private ResidentMap<long, long> DroneControllers() =>
        _droneControllers ??= Loading(() =>
        {
            return owner.Economy.Inspect(world =>
                world
                    .Set<WorldDrone>()
                    .Where(d => d.ControllerShipId != 0 && d.SystemId != null && (systemId == null || d.SystemId == systemId))
                    .Select(d => new DroneController(d.ControllerShipId, d.PilotId))
                    .Distinct()
                    .ToDictionary(d => d.ControllerShipId, d => d.PilotId)
            );
        });

    internal bool FireNpc(
        long pilotId,
        long shipId,
        long npcId,
        SpacePosition velocity,
        long now,
        double roll,
        int stamp,
        out NpcShot? shot
    )
    {
        var result = Execute(
            () =>
            {
                bool handled = FireNpcOwned(pilotId, shipId, npcId, velocity, now, roll, stamp, out var paid);
                return (Handled: handled, Shot: paid);
            },
            nameof(FireNpc)
        );
        shot = result.Shot;
        return result.Handled;
    }

    private bool FireNpcOwned(
        long pilotId,
        long shipId,
        long npcId,
        SpacePosition velocity,
        long now,
        double roll,
        int stamp,
        out NpcShot? shot
    )
    {
        using var ownership = owner.Enter();
        shot = null;
        if (!CanRun)
        {
            return false;
        }
        Trim();
        var ship = Ship(pilotId, now);
        if (!database.MotionStamps.TryGetValue(ship.SystemId, out int motion) || motion != stamp)
        {
            return false;
        }
        var aggression = Aggression().GetValueOrDefault(npcId);
        if (aggression is null || aggression.ShipId != shipId)
        {
            return true;
        }
        var npc = Npc(npcId, now);
        if (npc is not null && (npc.Role == NpcRole.MissionStructure || NpcPopulationRules.IsCivilian(npc.Role) || npc.Transit is not null))
        {
            return true;
        }
        if (
            ship.ShipId != shipId
            || ship.Position is null
            || ship.GroupId == FleetDefinitions.CapsuleGroup
            || npc is null
            || !npc.Available
            || npc.SystemId != ship.SystemId
        )
        {
            return false;
        }
        if (!PlayerEwarRules.CanTarget(npc.IncomingElectronicWarfare, shipId))
        {
            return true;
        }
        var attributes = npc.Attributes;
        if (
            attributes.TryGetValue(DogmaAttributeId.MaxTargetRange, out double targetingRange)
            && targetingRange > 0
            && ship.Position.DistanceTo(npc.Position) - ship.Radius - npc.Radius > targetingRange
        )
        {
            return true;
        }
        if (attributes.GetValueOrDefault(DogmaAttributeId.EntityMissileType) > 0)
        {
            return now < Math.Min(aggression.ReadyAt, aggression.MissileReadyAt);
        }
        double leash = NpcPopulationRules.Leash(attributes, npc.MissileRange, npc.AcquisitionRange);
        if (ship.Position.DistanceTo(npc.Home) > leash || npc.Position.DistanceTo(npc.Home) > leash)
        {
            return false;
        }
        if (now >= aggression.ReadyAt && NpcPopulationRules.HasDirectWeapon(attributes) && DroneControllers().ContainsKey(shipId))
        {
            if (!FireAtDrone(ship, npc, aggression, now, ref roll, out shot))
            {
                return false;
            }
            if (shot is not null)
            {
                return true;
            }
        }
        if (
            !NpcPopulationRules.HasDirectWeapon(attributes)
            || now < aggression.ReadyAt
            || ship.Position.DistanceTo(npc.Position) - ship.Radius - npc.Radius
                > attributes.GetValueOrDefault(DogmaAttributeId.EntityAttackRange)
        )
        {
            return true;
        }
        var paid = LiveNpcCombatRules.Fire(ship, npc, velocity, now, roll);
        if (paid.Shot.Health.Remaining.Destroyed)
        {
            // The durable action settles capsule, insurance, inventory and report together.
            return false;
        }
        _ships[pilotId] = paid.Ship with
        {
            NpcAggression = CrimewatchRules.Extend(
                paid.Ship.NpcAggression ?? new(),
                checked(now + (long)Math.Ceiling(paid.Shot.Weapon.Duration * TimeSpan.TicksPerMillisecond)),
                CrimewatchRules.NpcDuration
            ),
        };
        _dirtyShips.Add(pilotId);
        _aggressions![npcId] = aggression with
        {
            ReadyAt = checked(now + (long)Math.Ceiling(paid.Shot.Weapon.Duration * TimeSpan.TicksPerMillisecond)),
            LastShotAt = now,
            LastTargetId = shipId,
        };
        _dirtyAggressions.Add(npcId);
        RecordDroneIncoming(shipId, npcId, now);
        owner.ObjectsChanged();
        shot = paid.Shot;
        return true;
    }
}
