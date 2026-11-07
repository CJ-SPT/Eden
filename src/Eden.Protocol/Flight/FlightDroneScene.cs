using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Protocol.Combat;
using Eden.Protocol.Drones;

namespace Eden.Protocol.Flight;

internal sealed class FlightDroneScene(SharedSpace space, TimeProvider clock, IPilotDrones? drones, Action<DestinyCommand> update)
{
    internal DroneContact[] Contacts { get; private set; } = [];

    internal void Capture(FlightSceneContext context) => Contacts = ReadDrones(context);

    private DroneContact[] ReadDrones(FlightSceneContext context) =>
        context.Pilot.Position is not { } position
            ? []
            : (drones?.List(context.Pilot.SystemId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()) ?? [])
                // A controller must keep simulating its flight even beyond the observer grid.
                .Where(d => d.ShipId == context.Pilot.ShipId || position.DistanceTo(d.Position) <= NpcEncounterDefinitions.VisibilityRange)
                .ToArray();

    internal void RefreshDrones(FlightSceneContext context)
    {
        if (!context.InBallpark)
        {
            return;
        }
        var current = ReadDrones(context);
        foreach (var departed in Contacts.ExceptBy(current.Select(d => d.Id), d => d.Id))
        {
            update(new DestinyCommand.DroneStateChanged(new(departed, Removed: true)));
            update(new DestinyCommand.RemoveBall(departed.Id, drones?.WasDestroyed(departed.Id) == true));
        }
        var added = current.ExceptBy(Contacts.Select(d => d.Id), d => d.Id).ToArray();
        if (added.Length > 0)
        {
            update(new DestinyCommand.AddBalls(Ballpark.AddDrones(added, space.Stamp(clock))));
        }
        var previous = Contacts.ToDictionary(d => d.Id);
        foreach (var drone in current)
        {
            var old = previous.GetValueOrDefault(drone.Id);
            if (old is null || old.ShipId != drone.ShipId || old.Activity != drone.Activity || old.TargetId != drone.TargetId)
            {
                // Michelle uses the control row's owner OR controller to keep a panel entry.
                // The physical slim keeps the owner for reconnect and scooping.
                if (drone.ShipId != 0 || old is { ShipId: not 0 })
                {
                    update(new DestinyCommand.DroneStateChanged(new(drone, Removed: drone.ShipId == 0)));
                }
                update(new DestinyCommand.OnSlimItemChange(drone.Id, new DroneSlimItem(drone)));
            }
            if (
                old is null
                || old.TargetId != drone.TargetId
                || (old.Activity == DroneActivity.ReturningToBay) != (drone.Activity == DroneActivity.ReturningToBay)
                || old.ShipId != drone.ShipId
                || old.Orbiting != drone.Orbiting
                || old.SpeedLimit != drone.SpeedLimit
            )
            {
                if (drone.ShipId != 0 && drone.Profile.Kind == CombatDroneKind.Sentry)
                {
                    update(new DestinyCommand.SetMaxSpeed(drone.Id, 0));
                    update(new DestinyCommand.SetSpeedFraction(drone.Id, 0));
                    update(new DestinyCommand.Stop(drone.Id));
                }
                else if (drone.TargetId != 0 && drone.ShipId != 0)
                {
                    update(new DestinyCommand.SetMaxSpeed(drone.Id, drone.SpeedLimit));
                    update(new DestinyCommand.SetSpeedFraction(drone.Id, 1));
                    update(
                        new DestinyCommand.Follow(
                            drone.Orbiting ? NavigationMode.Orbit : NavigationMode.Follow,
                            drone.Id,
                            drone.TargetId,
                            drone.Activity == DroneActivity.ReturningToBay ? 0 : drone.Profile.OrbitRange
                        )
                    );
                }
                else if (old is { ShipId: not 0 })
                {
                    // Anchor a newly released drone once at its final authoritative position.
                    update(new DestinyCommand.SetBallPosition(drone.Id, drone.Position.X, drone.Position.Y, drone.Position.Z));
                    update(new DestinyCommand.SetBallVelocity(drone.Id, 0, 0, 0));
                    update(new DestinyCommand.Stop(drone.Id));
                }
            }
            if (old is not null && old.Health != drone.Health)
            {
                update(new DestinyCommand.OnDamageStateChange(drone.Id, drone.Health.DamageState()));
            }
            if (drone.ShotAt != 0 && old?.ShotAt != drone.ShotAt)
            {
                update(
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
        Contacts = current;
    }
}
