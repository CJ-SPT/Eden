using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Flight;

internal sealed class FlightSession(IShipPositionStore world, DockedPilot pilot, TimeProvider clock, SharedSpace space, IReadOnlyList<StaticSpaceObject> objects) : IDisposable
{
    private ShipContact[] _peers = [];
    private int _lastPeerSync;
    public bool InBallpark { get; private set; }
    private readonly long _start = clock.GetTimestamp();
    private double Elapsed => clock.GetElapsedTime(_start).TotalSeconds;
    private double _sampleTime;
    private SpacePosition _lastHeading = new(1, 0, 0);
    private SpacePosition? _direction;
    private SpacePosition _velocity = new(0, 0, 0);
    private SpacePosition? _approachPoint;
    private double _followRange;
    private SpacePosition? _warpDestination;
    private double _warpLaunchTime;
    private WarpCourse? _warp;
    private double _speedFraction = 1;
    public DockedPilot Pilot { get; private set; } = pilot;
    public List<object?> Updates { get; } = [];
    public bool Moving => _direction is not null || _warp is not null || _velocity != SpacePosition.Zero;

    public void Checkpoint()
    {
        if (_warpDestination is { } end && Elapsed >= _warpLaunchTime)
        {
            var origin = Pilot.Position!;
            double distance = origin.DistanceTo(end);
            // Seed the aligned velocity at handoff. Contact with a station hull can otherwise
            // hold the client below Destiny's 75% entry threshold while the server advances.
            double scale = Pilot.Flight.MaxSpeed * 0.8 / distance;
            _velocity = new((end.X - origin.X) * scale, (end.Y - origin.Y) * scale, (end.Z - origin.Z) * scale);
            _direction = null;
            _warpDestination = null;
            _warp = new(origin, end, Elapsed, Pilot.Flight);
            ApplyCourse(Sample);
            return;
        }
        if (_warp is { } warp)
        {
            var sample = warp.Sample(Elapsed);
            if (sample != Pilot.Position)
            {
                SetPosition(sample);
            }
            if (Elapsed < warp.ExitTime)
            {
                return;
            }
            _warp = null;
            WarpEffect(Pilot.ShipId, false);
            _velocity = warp.ExitVelocity;
            _sampleTime = warp.ExitTime;
            // Destiny exits warp and brakes locally. A timed velocity or position override can cut the tunnel short.
        }
        if (!Moving || Pilot.Position is not { } position)
        {
            return;
        }
        var direction = _direction ?? SpacePosition.Zero;
        double now = Elapsed;
        double seconds = now - _sampleTime;
        _sampleTime = now;
        // Integrate Destiny's linear drag towards cruise velocity (mass * agility / friction).
        double tau = Pilot.Flight.InertiaTime;
        double decay = Math.Exp(-seconds / tau);
        double cruise = Pilot.Flight.MaxSpeed * _speedFraction;
        double transient = tau * (1 - decay);
        SpacePosition next = new(position.X + direction.X * cruise * (seconds - transient) + _velocity.X * transient,
            position.Y + direction.Y * cruise * (seconds - transient) + _velocity.Y * transient,
            position.Z + direction.Z * cruise * (seconds - transient) + _velocity.Z * transient);
        _velocity = new(direction.X * cruise * (1 - decay) + _velocity.X * decay,
            direction.Y * cruise * (1 - decay) + _velocity.Y * decay,
            direction.Z * cruise * (1 - decay) + _velocity.Z * decay);
        if (_approachPoint is { } target &&
            (target.X - next.X) * direction.X + (target.Y - next.Y) * direction.Y + (target.Z - next.Z) * direction.Z <= 0)
        {
            next = target;
            _direction = null;
            _velocity = new(0, 0, 0);
        }
        if (StationContact(position, next) is { } contact)
        {
            SetPosition(contact);
            if (_warpDestination is null)
            {
                if (_direction is not null)
                {
                    Update("Stop", Pilot.ShipId);
                }
                SetAtRest();
            }
            return;
        }
        if ((_direction is null || _speedFraction == 0) && _velocity.DistanceTo(SpacePosition.Zero) < 0.05)
        {
            // Save the asymptote without moving or rotating a client that is already braking.
            next = new(next.X + _velocity.X * tau, next.Y + _velocity.Y * tau, next.Z + _velocity.Z * tau);
            SetAtRest();
        }
        SetPosition(next);
    }

    private ShipContact Sample => new(Pilot, _velocity, _direction, _speedFraction,
        _approachPoint is null ? null : _followRange, _warp);

    // Clip the complete checkpoint segment so even a delayed tick cannot cross the station.
    private SpacePosition? StationContact(SpacePosition start, SpacePosition end)
    {
        double x = start.X - Pilot.X, y = start.Y - Pilot.Y, z = start.Z - Pilot.Z;
        double dx = end.X - start.X, dy = end.Y - start.Y, dz = end.Z - start.Z;
        double along = x * dx + y * dy + z * dz;
        if (along >= 0)
        {
            return null;
        }
        double radius = Pilot.Flight.StationRadius + Pilot.Flight.ShipRadius;
        double lengthSquared = dx * dx + dy * dy + dz * dz;
        double clearance = Math.Max(0, x * x + y * y + z * z - radius * radius);
        double discriminant = along * along - lengthSquared * clearance;
        if (discriminant <= 0)
        {
            return null;
        }
        double fraction = clearance / (-along + Math.Sqrt(discriminant));
        return fraction <= 1 ? new(start.X + dx * fraction, start.Y + dy * fraction, start.Z + dz * fraction) : null;
    }

    public void RefreshPeers()
    {
        space.Publish(Sample);
        var current = space.Peers(Pilot);
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
                if (ship.Heading is not null || ship.Warp is not null)
                {
                    ApplyCourse(ship);
                }
                continue;
            }
            if (ship.Warp is not null)
            {
                if (ship.Warp != previous.Warp)
                {
                    ApplyCourse(ship);
                }
                continue;
            }
            if (ship.Heading is null)
            {
                if (previous.Warp is not null)
                {
                    WarpEffect(ship.Pilot.ShipId, false);
                }
                else if (previous.Heading is not null)
                {
                    Update("Stop", ship.Pilot.ShipId);
                }
                continue;
            }
            if (ship.Heading != previous.Heading || ship.SpeedFraction != previous.SpeedFraction ||
                ship.FollowRange != previous.FollowRange || reconcile)
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
        Pilot = Pilot with { Position = position };
    }

    private double DistanceToStation(SpacePosition position)
    {
        double dx = position.X - Pilot.X, dy = position.Y - Pilot.Y, dz = position.Z - Pilot.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private int Stamp => (int)Elapsed + 1;

    public void Snapshot()
    {
        var position = Pilot.Position!;
        double separation = DistanceToStation(position);
        double surface = Pilot.Flight.StationRadius + Pilot.Flight.ShipRadius;
        if (separation < surface - 0.01)
        {
            // Older worlds could save a ship inside the station. Recover just outside its hull.
            double scale = (surface + 1) / Math.Max(separation, 1);
            SetPosition(separation < 1 ? new(Pilot.X + surface + 1, Pilot.Y, Pilot.Z) :
                new(Pilot.X + (position.X - Pilot.X) * scale, Pilot.Y + (position.Y - Pilot.Y) * scale,
                    Pilot.Z + (position.Z - Pilot.Z) * scale));
        }
        InBallpark = true;
        space.Publish(Sample);
        Snapshot(space.Peers(Pilot));
    }

    private void Snapshot(ShipContact[] peers)
    {
        _peers = peers;
        int stamp = Stamp;
        Updates.Add(new object?[] { stamp, new object?[] { "SetState", new object?[] { Ballpark.Snapshot(Pilot, stamp, objects, peers) } } });
        if (Moving)
        {
            ApplyCourse(Sample);
        }
        foreach (var peer in peers.Where(ship => ship.Heading is not null || ship.Warp is not null))
        {
            ApplyCourse(peer);
        }
    }

    private void ApplyCourse(ShipContact ship)
    {
        long id = ship.Pilot.ShipId;
        if (ship.Warp is { } warp)
        {
            var end = warp.Destination;
            Update("SetSpeedFraction", id, 1d);
            Update("SetBallVelocity", id, ship.Velocity.X, ship.Velocity.Y, ship.Velocity.Z);
            Update("WarpTo", id, end.X, end.Y, end.Z, 0d, warp.WireFactor);
            WarpEffect(id, true);
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
                Update("FollowBall", id, ship.Pilot.StationId, range);
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
        _approachPoint = null;
        _velocity = new(0, 0, 0);
        _speedFraction = 0;
    }

    private void Update(string method, params object?[] args) =>
        Updates.Add(new object?[] { Stamp, new object?[] { method, args } });

    private object? StartWarp(long target, double range)
    {
        var body = objects.FirstOrDefault(item => item.Id == target);
        if (body is null)
        {
            return PilotCallRouter.Unsupported;
        }
        var center = body.Position;
        double radius = body.Radius;
        if (!InBallpark || !double.IsFinite(range) || range is < 0 or > 100_000)
        {
            return PilotCallRouter.Unsupported;
        }
        Checkpoint();
        var origin = Pilot.Position!;
        double distance = origin.DistanceTo(center);
        if (distance - radius - Pilot.Flight.ShipRadius < 150_000)
        {
            return PilotCallRouter.Unsupported;
        }
        // Separate the two landing lanes to keep stopped ships from occupying the same ball.
        double offset = (range + radius + Pilot.Flight.ShipRadius + (Pilot.ShipId % 2 == 0 ? 1500 : 500)) / distance;
        SpacePosition end = new(center.X + (origin.X - center.X) * offset,
            center.Y + (origin.Y - center.Y) * offset, center.Z + (origin.Z - center.Z) * offset);
        if (origin.DistanceTo(end) < 101_000)
        {
            // Destiny substitutes sublight approach for warp legs below 100 km.
            return PilotCallRouter.Unsupported;
        }
        double alignTime = Math.Log(4) * Pilot.Flight.InertiaTime;
        _warpDestination = end;
        _warpLaunchTime = Elapsed + alignTime;
        _direction = new((center.X - origin.X) / distance, (center.Y - origin.Y) / distance, (center.Z - origin.Z) / distance);
        _lastHeading = _direction;
        _speedFraction = 1;
        _sampleTime = Elapsed;
        _approachPoint = null;
        ApplyCourse(Sample);
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
        if (_warpDestination is not null && method == "CmdStop")
        {
            _warpDestination = null;
        }
        if ((_warp is not null || _warpDestination is not null) && method != "UpdateStateRequest")
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
                Checkpoint();
                _approachPoint = null;
                _lastHeading = new(x / length, y / length, z / length);
                _direction = _lastHeading;
                _sampleTime = Elapsed;
                if (_speedFraction == 0)
                {
                    _speedFraction = 1;
                }
                Update("GotoDirection", Pilot.ShipId, x, y, z);
                Update("SetSpeedFraction", Pilot.ShipId, _speedFraction);
                return null;
            case "CmdFollowBall" when arguments is [long target, var distance] && target == Pilot.StationId &&
                distance is double or long:
                double standOff = Convert.ToDouble(distance);
                if (standOff is < 0 or > 1_000_000 || !double.IsFinite(standOff))
                {
                    return PilotCallRouter.Unsupported;
                }
                Checkpoint();
                position = Pilot.Position!;
                double separation = DistanceToStation(position);
                if (separation > 0)
                {
                    double radius = Pilot.Flight.StationRadius + Pilot.Flight.ShipRadius + standOff;
                    double sign = separation >= radius ? 1 : -1;
                    _direction = new(sign * (Pilot.X - position.X) / separation, sign * (Pilot.Y - position.Y) / separation,
                        sign * (Pilot.Z - position.Z) / separation);
                    _lastHeading = _direction;
                    _approachPoint = new(Pilot.X + (position.X - Pilot.X) * radius / separation,
                        Pilot.Y + (position.Y - Pilot.Y) * radius / separation, Pilot.Z + (position.Z - Pilot.Z) * radius / separation);
                    _followRange = standOff;
                    _speedFraction = 1;
                    _sampleTime = Elapsed;
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
                Checkpoint();
                if (_direction is null && fraction > 0)
                {
                    _direction = _lastHeading;
                    _approachPoint = null;
                    _sampleTime = Elapsed;
                    Update("GotoDirection", Pilot.ShipId, _lastHeading.X, _lastHeading.Y, _lastHeading.Z);
                }
                _speedFraction = fraction;
                Update("SetSpeedFraction", Pilot.ShipId, fraction);
                return null;
            case "CmdStop":
                Checkpoint();
                _direction = null;
                _approachPoint = null;
                _speedFraction = 0;
                _sampleTime = Elapsed;
                Update("Stop", Pilot.ShipId);
                return null;
            case "CmdDock" when arguments is [long station, long ship] && station == Pilot.StationId && ship == Pilot.ShipId:
                Checkpoint();
                position = Pilot.Position!;
                if (DistanceToStation(position) > Pilot.Flight.StationRadius + Pilot.Flight.ShipRadius + 2500)
                {
                    return PilotCallRouter.Unsupported;
                }
                SetPosition(null);
                LeaveSpace();
                _direction = null;
                _velocity = new(0, 0, 0);
                _approachPoint = null;
                _speedFraction = 1;
                _lastHeading = new(1, 0, 0);
                return null;
            default:
                return PilotCallRouter.Unsupported;
        }
    }
}
