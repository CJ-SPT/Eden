using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Protocol.Combat;
using Eden.Protocol.Drones;

namespace Eden.Protocol.Flight;

internal sealed partial class FlightSession
{
    private DroneContact[] _drones = [];
    internal IReadOnlyList<DroneContact> VisibleDrones => _drones;

    private DroneContact[] ReadDrones() =>
        Pilot.Position is not { } position
            ? []
            : (drones?.List(Pilot.SystemId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()) ?? [])
                .Where(d => position.DistanceTo(d.Position) <= NpcEncounterDefinitions.VisibilityRange)
                .ToArray();

    public void RefreshDrones()
    {
        if (!InBallpark)
        {
            return;
        }
        var current = ReadDrones();
        foreach (var departed in _drones.ExceptBy(current.Select(d => d.Id), d => d.Id))
        {
            Update(new DestinyCommand.DroneStateChanged(new(departed, Removed: true)));
            Update(new DestinyCommand.RemoveBall(departed.Id, drones?.WasDestroyed(departed.Id) == true));
        }
        var added = current.ExceptBy(_drones.Select(d => d.Id), d => d.Id).ToArray();
        if (added.Length > 0)
        {
            Update(new DestinyCommand.AddBalls(Ballpark.AddDrones(added, Stamp)));
        }
        foreach (var drone in current)
        {
            var old = _drones.FirstOrDefault(d => d.Id == drone.Id);
            if (old is null || old.ShipId != drone.ShipId || old.Activity != drone.Activity || old.TargetId != drone.TargetId)
            {
                Update(new DestinyCommand.DroneStateChanged(new(drone)));
                Update(new DestinyCommand.OnSlimItemChange(drone.Id, new DroneSlimItem(drone)));
            }
            if (
                old is null
                || old.Position != drone.Position
                || old.TargetId != drone.TargetId
                || old.Activity != drone.Activity
                || old.ShipId != drone.ShipId
                || old.Orbiting != drone.Orbiting
                || old.SpeedLimit != drone.SpeedLimit
            )
            {
                Update(new DestinyCommand.SetBallPosition(drone.Id, drone.Position.X, drone.Position.Y, drone.Position.Z));
                Update(new DestinyCommand.SetBallVelocity(drone.Id, drone.Velocity.X, drone.Velocity.Y, drone.Velocity.Z));
                if (drone.TargetId != 0 && drone.ShipId != 0)
                {
                    Update(new DestinyCommand.SetMaxSpeed(drone.Id, drone.SpeedLimit));
                    Update(new DestinyCommand.SetSpeedFraction(drone.Id, 1));
                    Update(
                        new DestinyCommand.Follow(
                            drone.Orbiting ? NavigationMode.Orbit : NavigationMode.Follow,
                            drone.Id,
                            drone.TargetId,
                            drone.Activity == DroneActivity.ReturningToBay ? 0 : drone.Profile.OrbitRange
                        )
                    );
                }
                else
                {
                    Update(new DestinyCommand.Stop(drone.Id));
                }
            }
            if (old is not null && old.Health != drone.Health)
            {
                Update(new DestinyCommand.OnDamageStateChange(drone.Id, drone.Health.DamageState()));
            }
            if (drone.ShotAt != 0 && old?.ShotAt != drone.ShotAt)
            {
                Update(
                    new DestinyCommand.TimedSpecialEffect(
                        drone.Id,
                        drone.Id,
                        drone.Profile.TypeId,
                        drone.ShotTargetId,
                        null,
                        "effects.Laser",
                        true,
                        true,
                        false,
                        drone.Profile.CycleMilliseconds,
                        drone.ShotAt
                    )
                );
            }
        }
        _drones = current;
    }
}
