using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;

namespace Eden.Protocol.Flight;

internal sealed partial class FlightSession
{
    private long _cloakGraceUntil;
    internal bool CanActivatePropulsion =>
        CanUseSelfModules && !InWarp && (!Cloaked || clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() < _cloakGraceUntil);

    internal bool HasCloakObstacle()
    {
        if (Pilot.Position is not { } own)
        {
            return true;
        }
        bool Near(SpacePosition position, double radius) => SpatialModuleRules.Proximity(own, Pilot.Flight.ShipRadius, position, radius);
        return Scene.Objects.Any(o => (o.Solid || MiningDefinitions.IsResource(o.CategoryId, o.GroupId)) && Near(o.Position, o.Radius))
            || space.Peers(Pilot).Any(p => Near(p.Pilot.Position!, p.Pilot.Flight.ShipRadius))
            || VisibleNpcs.Any(n => Near(n.Position, n.Radius))
            || VisibleWrecks.Any(w => Near(w.Position, w.Radius))
            || VisibleDrones.Any(d => Near(d.Position, d.Profile.Radius));
    }

    internal void SpatialEffect(SpatialActivation activation, bool active)
    {
        if (activation.Module.Cloak)
        {
            _cloakGraceUntil = active
                ? SpatialModuleRules.Deadline(activation.StartedAt, SpatialModuleRules.ActivationGraceMilliseconds)
                : 0;
            Update(active ? new DestinyCommand.CloakBall(Pilot.ShipId) : new DestinyCommand.UncloakBall(Pilot.ShipId));
            Update(
                new DestinyCommand.SpecialEffect(
                    Pilot.ShipId,
                    null,
                    null,
                    null,
                    null,
                    active ? "effects.Cloak" : "effects.Uncloak",
                    false,
                    true,
                    false
                )
            );
        }
        else
        {
            _microJump = active ? activation : null;
            Update(MicroJumpEffect(Pilot.ShipId, activation, active));
        }
        PublishMotion();
        PublishContact();
    }

    internal static DestinyCommand MicroJumpEffect(long ship, SpatialActivation activation, bool active) =>
        new DestinyCommand.TimedSpecialEffect(
            ship,
            activation.Module.ItemId,
            activation.Module.TypeId,
            null,
            null,
            "effects.MicroJumpDriveEngage",
            false,
            active,
            active,
            activation.Module.DurationMilliseconds,
            activation.StartedAt
        );

    internal void AdoptMicroJump(MicroJumpResult jump)
    {
        // World committed the position before this observer publication. Preserve the paid heading/velocity;
        // SetBallPosition also resets native interpolation and spatial membership (Carbon Ballpark.cpp).
        Pilot = Pilot with
        {
            Position = jump.Position,
        };
        _navigation.SetVelocity(jump.Velocity);
        Update(new DestinyCommand.SetBallPosition(Pilot.ShipId, jump.Position.X, jump.Position.Y, jump.Position.Z));
        Update(new DestinyCommand.SetBallVelocity(Pilot.ShipId, jump.Velocity.X, jump.Velocity.Y, jump.Velocity.Z));
        Update(new DestinyCommand.MicroJumpVisual(Pilot.ShipId, jump.Position));
        PublishMotion();
        PublishContact();
    }
}
