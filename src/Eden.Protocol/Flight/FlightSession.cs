using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Flight;

internal sealed class FlightSession(IShipPositionStore world, DockedPilot pilot, TimeProvider clock, SharedSpace space, IReadOnlyList<StaticSpaceObject> objects) : IDisposable
{
    private ShipContact[] _peers = [];
    private int _lastPeerSync;
    public bool InBallpark { get; private set; }
    private int _simulatedStamp = space.Stamp(clock);
    private int Stamp => space.Stamp(clock);
    private SpacePosition _lastHeading = new(1, 0, 0);
    private SpacePosition? _direction;
    private SpacePosition _velocity = new(0, 0, 0);
    private StaticSpaceObject? _followTarget;
    private sealed record GateTransit(int SourceId, StaticSpaceObject Exit, FlightSystem System, IReadOnlyList<StaticSpaceObject> Objects, int TransferStamp);
    // Build 3662168 client/effects defines effects.JumpOut with duration 10,000 ms.
    private const int JumpDurationTicks = 10;
    private GateTransit? _jump;
    private bool _awaitingArrival;
    private int? _arrivalStamp;
    private double _followRange;
    private SpacePosition? _warpDestination;
    private int _alignStamp;
    private WarpCourse? _warp;
    private double _speedFraction;
    public DockedPilot Pilot { get; private set; } = pilot;
    public List<object?> Updates { get; } = [];
    public bool Moving => _direction is not null || _warp is not null || _velocity != SpacePosition.Zero;

    public void Checkpoint()
    {
        int now = Stamp;
        var previous = Pilot.Position;
        while (_simulatedStamp < now && Moving && Pilot.Position is { } position)
        {
            _simulatedStamp++;
            Evolve(position);
        }
        _simulatedStamp = now;
        if (Pilot.Position != previous)
        {
            world.SetPosition(Pilot.ShipId, Pilot.Position);
        }
    }

    private void Evolve(SpacePosition position)
    {
        if (_warpDestination is { } destination)
        {
            _direction = (destination - position).Unit;
            double speed = _velocity.Length;
            if ((speed > Pilot.Flight.MaxSpeed * 0.75 && _velocity.Unit.Dot(_direction) > 0.99) ||
                _simulatedStamp - _alignStamp > 180)
            {
                _warp = new(position, destination, _simulatedStamp, Pilot.Flight, speed);
                _warpDestination = null;
                _direction = null;
            }
        }
        bool justExited = false;
        if (_warp is { } warp)
        {
            var sample = warp.Sample(_simulatedStamp, Math.Min(Pilot.Flight.MaxSpeed / 2, 100));
            position = sample.Position;
            Pilot = Pilot with { Position = position };
            _velocity = sample.Velocity;
            if (!sample.Exited)
            {
                return;
            }
            _warp = null;
            _speedFraction = 0;
            justExited = true;
            WarpEffect(Pilot.ShipId, false);
        }

        double cruise = Pilot.Flight.MaxSpeed * _speedFraction;
        if (_followTarget is { } follow)
        {
            var radial = position - follow.Position;
            double error = radial.Length - follow.Radius - Pilot.Flight.ShipRadius - _followRange;
            _direction = radial.Unit * -Math.Sign(error);
            if (cruise > 0 && Math.Abs(error) < cruise)
            {
                // Destiny GotoThrust tapers within one tick's travel distance.
                cruise *= Math.Pow(error / cruise, 4);
            }
        }
        // Build 3662168 accepts the legacy free-ball layout (no orientation fields).
        // EvolveStop damps Y before drag on each STOP tick, except the warp-exit integration.
        if (_direction is null && !justExited)
        {
            _velocity = _velocity with { Y = _velocity.Y * (0.93 / 1.07) };
        }
        double tau = Pilot.Flight.InertiaTime;
        double decay = Math.Exp(-1 / tau);
        var targetVelocity = (_direction ?? SpacePosition.Zero) * cruise;
        var next = position + (targetVelocity + (_velocity - targetVelocity) * (tau * (1 - decay)));
        _velocity = targetVelocity + (_velocity - targetVelocity) * decay;
        if (FirstContact(position, next) is { } contact)
        {
            next = contact;
            _velocity = SpacePosition.Zero;
            if (_warpDestination is null)
            {
                Update("SetBallPosition", Pilot.ShipId, contact.X, contact.Y, contact.Z);
                Update("Stop", Pilot.ShipId);
                Update("SetBallVelocity", Pilot.ShipId, 0d, 0d, 0d);
                SetAtRest();
            }
        }
        else if ((_direction is null || _speedFraction == 0) && _velocity.Length < 0.01)
        {
            SetAtRest();
        }
        Pilot = Pilot with { Position = next };
    }

    private ShipContact Sample => new(Pilot, _velocity, _direction, _speedFraction,
        _followTarget is null ? null : _followRange, _warp, _warpDestination, _alignStamp, (int)Math.Round(Pilot.Flight.WarpSpeed * 1000),
        _followTarget?.Id ?? 0, _jump?.SourceId, Stamp - _arrivalStamp < 8);

    private SpacePosition? FirstContact(SpacePosition start, SpacePosition end)
    {
        var delta = end - start;
        double lengthSquared = delta.Dot(delta);
        double earliest = double.PositiveInfinity;
        foreach (var body in objects)
        {
            var radial = start - body.Position;
            double along = radial.Dot(delta);
            if (along >= 0)
            {
                continue;
            }
            double radius = body.Radius + Pilot.Flight.ShipRadius;
            double clearance = Math.Max(0, radial.Dot(radial) - radius * radius);
            double discriminant = along * along - lengthSquared * clearance;
            if (discriminant > 0)
            {
                earliest = Math.Min(earliest, clearance / (-along + Math.Sqrt(discriminant)));
            }
        }
        return earliest <= 1 ? start + delta * earliest : null;
    }

    public void RefreshPeers()
    {
        space.Publish(Sample);
        var current = space.Peers(Pilot);
        if (current.Any(ship => ship.Warp is not null && Array.Exists(_peers, old => old.Pilot.ShipId == ship.Pilot.ShipId &&
            old.Warp != ship.Warp && old.AlignTo != ship.Warp.Destination)))
        {
            Snapshot(current);
            return;
        }
        foreach (var departed in _peers.ExceptBy(current.Select(ship => ship.Pilot.ShipId), ship => ship.Pilot.ShipId))
        {
            Update("RemoveBall", departed.Pilot.ShipId, false);
        }
        // Destiny integrates the same course between changes. Occasionally reconcile drift.
        bool reconcile = Stamp - _lastPeerSync >= 10;
        foreach (var ship in current)
        {
            var previous = Array.Find(_peers, peer => peer.Pilot.ShipId == ship.Pilot.ShipId);
            if (previous is null)
            {
                // AddBalls takes a single chunk. Replacing the entire park resets the observer's origin.
                Update("AddBalls", [Ballpark.AddShip(ship, Stamp)]);
                if (ship.Arriving)
                {
                    ArrivalEffect(ship.Pilot.ShipId);
                }
                // The client plays the arrival flash when it loads a ship already in warp.
                continue;
            }
            if (ship.DepartureGate is int departure && previous.DepartureGate != departure)
            {
                DepartureEffect(ship.Pilot.ShipId, departure, objects.Single(o => o.Id == departure).DestinationSystemId!.Value);
            }
            if (ship.Warp is not null)
            {
                if (ship.Warp != previous.Warp)
                {
                    WarpEffect(ship.Pilot.ShipId, true);
                }
                continue;
            }
            if (ship.AlignTo is not null)
            {
                if (ship.AlignTo != previous.AlignTo)
                {
                    ApplyCourse(ship);
                }
                continue;
            }
            if (previous.Warp is not null)
            {
                WarpEffect(ship.Pilot.ShipId, false);
            }
            if (ship.Heading is null)
            {
                if (previous.Heading is not null)
                {
                    Update("Stop", ship.Pilot.ShipId);
                }
                continue;
            }
            if ((ship.FollowRange is null && ship.Heading != previous.Heading) || ship.SpeedFraction != previous.SpeedFraction ||
                ship.FollowRange != previous.FollowRange || ship.FollowTargetId != previous.FollowTargetId || reconcile)
            {
                var position = ship.Pilot.Position!;
                Update("SetBallPosition", ship.Pilot.ShipId, position.X, position.Y, position.Z);
                ApplyCourse(ship);
            }
        }
        if (reconcile)
        {
            _lastPeerSync = Stamp;
        }
        _peers = current;
    }

    public IEnumerable<DockedPilot> VisiblePilots() => space.Peers(Pilot).Select(ship => ship.Pilot).Prepend(Pilot);

    private void LeaveSpace()
    {
        InBallpark = false;
        _peers = [];
        space.Leave(Pilot.ShipId);
    }

    public void Dispose()
    {
        try
        {
            Checkpoint();
        }
        finally
        {
            LeaveSpace();
        }
    }

    private void SetPosition(SpacePosition? position)
    {
        world.SetPosition(Pilot.ShipId, position);
        Pilot = Pilot with { Position = position, CurrentSystem = position is null ? null : Pilot.CurrentSystem };
    }

    private double DistanceToStation(SpacePosition position)
    {
        double dx = position.X - Pilot.X, dy = position.Y - Pilot.Y, dz = position.Z - Pilot.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public void Snapshot()
    {
        Checkpoint();
        if (!InBallpark && Pilot.Position is { } position)
        {
            // A legacy save or a disconnect during warp can leave the ship inside a rigid body.
            foreach (var body in objects)
            {
                var radial = position - body.Position;
                double surface = body.Radius + Pilot.Flight.ShipRadius;
                if (radial.Length < surface - 0.01)
                {
                    position = body.Position + (radial.Length < 1 ? new SpacePosition(1, 0, 0) : radial.Unit) * (surface + 1);
                    SetPosition(position);
                }
            }
        }
        bool entering = !InBallpark;
        InBallpark = true;
        bool arriving = entering && _awaitingArrival;
        if (arriving)
        {
            _awaitingArrival = false;
            _arrivalStamp = Stamp;
        }
        space.Publish(Sample);
        Snapshot(space.Peers(Pilot));
        if (arriving)
        {
            ArrivalEffect(Pilot.ShipId);
        }
    }

    private void Snapshot(ShipContact[] peers)
    {
        _peers = peers;
        int stamp = Stamp;
        Updates.Add(new object?[] { stamp, new object?[] { "SetState", new object?[] { Ballpark.Snapshot(Sample, stamp, objects, peers) } } });
        if (_warp is not null || _warpDestination is not null)
        {
            WarpEffect(Pilot.ShipId, true);
        }
    }

    private void ApplyCourse(ShipContact ship)
    {
        long id = ship.Pilot.ShipId;
        if (ship.AlignTo is { } end)
        {
            Update("SetSpeedFraction", id, 1d);
            Update("SetBallVelocity", id, ship.Velocity.X, ship.Velocity.Y, ship.Velocity.Z);
            Update("WarpTo", id, end.X, end.Y, end.Z, 0d, ship.WarpFactor);
            return;
        }
        if (ship.Heading is null)
        {
            Update("Stop", id);
        }
        else
        {
            if (ship.FollowRange is double range)
            {
                Update("FollowBall", id, ship.FollowTargetId, range);
            }
            else
            {
                Update("GotoDirection", id, ship.Heading.X, ship.Heading.Y, ship.Heading.Z);
            }
            Update("SetSpeedFraction", id, ship.SpeedFraction);
        }
        Update("SetBallVelocity", id, ship.Velocity.X, ship.Velocity.Y, ship.Velocity.Z);
    }

    private void WarpEffect(long id, bool start)
    {
        // The simulation command moves the ball. This separate toggle drives the pilot's tunnel and peer flash.
        Update("OnSpecialFX", id, null, null, null, null, "effects.Warping", false, start, true);
    }

    private void SetAtRest()
    {
        _direction = null;
        _followTarget = null;
        _velocity = new(0, 0, 0);
        _speedFraction = 0;
    }

    private void Update(string method, params object?[] args) =>
        Updates.Add(new object?[] { _simulatedStamp, new object?[] { method, args } });

    public object? StartJump(object?[] args, ISystemObjects systems)
    {
        Checkpoint();
        if (!InBallpark || Pilot.Position is not { } position || _jump is not null || _warp is not null ||
            _warpDestination is not null || args is not [long sourceId, long exitId, long shipId] || shipId != Pilot.ShipId ||
            objects.FirstOrDefault(o => o.Id == sourceId) is not { DestinationSystemId: int systemId } source ||
            source.DestinationGateId != exitId || position.DistanceTo(source.Position) > source.Radius + Pilot.Flight.ShipRadius + 2500)
        {
            return PilotCallRouter.Unsupported;
        }
        var destination = systems.Load(systemId);
        var exit = destination.FirstOrDefault(o => o.Id == exitId && o.DestinationGateId == source.Id && o.DestinationSystemId == Pilot.SystemId);
        if (exit is null)
        {
            return PilotCallRouter.Unsupported;
        }
        // Keep the source balls alive while the jump camera turns, fades the UI, and enters the tunnel.
        _arrivalStamp = null;
        _jump = new(source.Id, exit, systems.GetSystem(systemId), destination, Stamp + JumpDurationTicks);
        SetAtRest();
        Update("Stop", Pilot.ShipId);
        DepartureEffect(Pilot.ShipId, source.Id, systemId);
        space.Publish(Sample);
        return null;
    }

    public bool CompleteJump()
    {
        if (_jump is not { } jump || Stamp < jump.TransferStamp)
        {
            return false;
        }
        var lane = Pilot.ShipId % 2 == 0 ? -1 : 1;
        var position = jump.Exit.Position + new SpacePosition(lane * (jump.Exit.Radius + Pilot.Flight.ShipRadius + 12_000), 0, 0);
        world.SetPosition(Pilot.ShipId, position, jump.System.Id);
        LeaveSpace();
        objects = jump.Objects;
        Pilot = Pilot with { Position = position, CurrentSystem = jump.System };
        _jump = null;
        _awaitingArrival = true;
        return true;
    }

    // SubwayService reads graphicInfo[0] to preload the skybox and attach the destination camera.
    private void DepartureEffect(long shipId, int gateId, int systemId) =>
        Update("OnSpecialFX", shipId, null, null, gateId, null, "effects.JumpOut", false, true, false,
            JumpDurationTicks * 1000, 0, null, 0, new object?[] { systemId });

    // SubwayService completes the gate transition. JumpIn supplies the ship flash.
    private void ArrivalEffect(long shipId) =>
        Update("OnSpecialFX", shipId, null, null, null, null, "effects.JumpIn", false, true, false);

    private object? StartWarp(long target, double range)
    {
        var body = objects.FirstOrDefault(item => item.Id == target);
        if (body is null)
        {
            return PilotCallRouter.Unsupported;
        }
        if (!InBallpark || !double.IsFinite(range) || range is < 0 or > 100_000)
        {
            return PilotCallRouter.Unsupported;
        }
        var origin = Pilot.Position!;
        var anchor = CelestialWarpPoint.Resolve(body, origin);
        if (origin.DistanceTo(anchor) - Pilot.Flight.ShipRadius < 150_000)
        {
            return PilotCallRouter.Unsupported;
        }
        var toward = (anchor - origin).Unit;
        double standOff = range + Pilot.Flight.ShipRadius + (Pilot.ShipId % 2 == 0 ? 1500 : 500);
        var end = anchor - toward * standOff;
        if (origin.DistanceTo(end) < 101_000)
        {
            return PilotCallRouter.Unsupported;
        }
        _warpDestination = end;
        _alignStamp = _simulatedStamp;
        _direction = toward;
        _lastHeading = _direction;
        _speedFraction = 1;
        _followTarget = null;
        ApplyCourse(Sample);
        // WarpLoop prepares the tunnel while aligning, then observes Destiny's positive
        // effect stamp to trigger the camera and acceleration sequence on the client.
        WarpEffect(Pilot.ShipId, true);
        space.Publish(Sample);
        return null;
    }

    public object? Call(string service, string method, object?[] arguments, Dictionary<string, object?>? keywords = null)
    {
        if (service == "ship" && method == "Undock" && Pilot.Position is null &&
            arguments is [long id, ..] && id == Pilot.ShipId)
        {
            // Separate the two local launch lanes even on simultaneous undocks.
            double lane = (Pilot.ShipId % 2 == 0) ? 1500 : 0;
            SetPosition(new(Pilot.X + Pilot.Flight.StationRadius + 500, Pilot.Y + lane, Pilot.Z));
            return null;
        }
        if (service != "beyonce" || Pilot.Position is not { } position)
        {
            return PilotCallRouter.Unsupported;
        }
        Checkpoint();
        if (_warpDestination is not null && method == "CmdStop")
        {
            _warpDestination = null;
            WarpEffect(Pilot.ShipId, false);
        }
        if ((_jump is not null || _warp is not null || _warpDestination is not null) && method != "UpdateStateRequest")
        {
            return PilotCallRouter.Unsupported;
        }
        switch (method)
        {
            case "CmdWarpToStuff" when arguments is [string or byte[], long targetId] &&
                BlueMarshal.Text(arguments[0]) == "item":
                var range = keywords?.GetValueOrDefault("minRange") ?? 0L;
                return range is double or long ? StartWarp(targetId, Convert.ToDouble(range)) : PilotCallRouter.Unsupported;
            case "UpdateStateRequest":
                Snapshot();
                return null;
            case "CmdGotoDirection" when arguments is [double x, double y, double z]:
                double length = Math.Sqrt(x * x + y * y + z * z);
                if (!(length > 0) || !double.IsFinite(length))
                {
                    return PilotCallRouter.Unsupported;
                }
                _followTarget = null;
                _lastHeading = new(x / length, y / length, z / length);
                _direction = _lastHeading;
                if (_speedFraction == 0)
                {
                    _speedFraction = 1;
                }
                Update("GotoDirection", Pilot.ShipId, x, y, z);
                Update("SetSpeedFraction", Pilot.ShipId, _speedFraction);
                return null;
            case "CmdFollowBall" when arguments is [long target, var distance] && objects.FirstOrDefault(o => o.Id == target) is { } follow &&
                distance is double or long:
                double standOff = Convert.ToDouble(distance);
                if (standOff is < 0 or > 1_000_000 || !double.IsFinite(standOff))
                {
                    return PilotCallRouter.Unsupported;
                }
                position = Pilot.Position!;
                double separation = position.DistanceTo(follow.Position);
                if (separation > 0)
                {
                    double radius = follow.Radius + Pilot.Flight.ShipRadius + standOff;
                    double sign = separation >= radius ? 1 : -1;
                    _direction = (follow.Position - position) * (sign / separation);
                    _lastHeading = _direction;
                    _followTarget = follow;
                    _followRange = standOff;
                    _speedFraction = 1;
                    Update("FollowBall", Pilot.ShipId, target, standOff);
                    Update("SetSpeedFraction", Pilot.ShipId, 1d);
                }
                return null;
            case "CmdSetSpeedFraction" when arguments is [var speed] && speed is double or long:
                double fraction = Convert.ToDouble(speed);
                if (!(fraction >= 0 && fraction <= 1))
                {
                    return PilotCallRouter.Unsupported;
                }
                if (_direction is null && fraction > 0)
                {
                    _direction = _lastHeading;
                    _followTarget = null;
                    Update("GotoDirection", Pilot.ShipId, _lastHeading.X, _lastHeading.Y, _lastHeading.Z);
                }
                _speedFraction = fraction;
                Update("SetSpeedFraction", Pilot.ShipId, fraction);
                return null;
            case "CmdStop":
                _direction = null;
                _followTarget = null;
                _speedFraction = 0;
                Update("Stop", Pilot.ShipId);
                return null;
            case "CmdDock" when arguments is [long station, long ship] && Pilot.SystemId == Pilot.HomeSystemId && station == Pilot.StationId && ship == Pilot.ShipId:
                position = Pilot.Position!;
                if (DistanceToStation(position) > Pilot.Flight.StationRadius + Pilot.Flight.ShipRadius + 2500)
                {
                    return PilotCallRouter.Unsupported;
                }
                SetPosition(null);
                LeaveSpace();
                _direction = null;
                _velocity = new(0, 0, 0);
                _followTarget = null;
                _speedFraction = 1;
                _lastHeading = new(1, 0, 0);
                return null;
            default:
                return PilotCallRouter.Unsupported;
        }
    }
}
