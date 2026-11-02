using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Flight;

internal sealed class FlightSession(IShipPositionStore world, DockedPilot pilot, TimeProvider clock, SharedSpace space) : IDisposable
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
    private double _speedFraction = 1;
    public DockedPilot Pilot { get; private set; } = pilot;
    public List<object?> Updates { get; } = [];
    public bool Moving => _direction is not null;

    public void Checkpoint()
    {
        if (_direction is not { } direction || Pilot.Position is not { } position)
        {
            return;
        }
        double now = Elapsed;
        double seconds = now - _sampleTime;
        _sampleTime = now;
        // Integrate Destiny's linear drag towards cruise velocity (mass * agility / friction).
        double tau = Pilot.Flight.Mass * Pilot.Flight.Agility / 1_000_000d;
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
            Stop();
            return;
        }
        SetPosition(next);
    }

    private ShipContact Sample => new(Pilot, _velocity, _direction, _speedFraction,
        _approachPoint is null ? null : _followRange);

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
                if (ship.Heading is not null)
                {
                    ApplyCourse(ship);
                }
                continue;
            }
            if (ship.Heading != previous.Heading || ship.SpeedFraction != previous.SpeedFraction ||
                ship.FollowRange != previous.FollowRange || (reconcile && ship.Heading is not null) ||
                (ship.Heading is null && ship.Pilot.Position != previous.Pilot.Position))
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
        Updates.Add(new object?[] { stamp, new object?[] { "SetState", new object?[] { Ballpark.Snapshot(Pilot, stamp, peers) } } });
        if (Moving)
        {
            ApplyCourse(Sample);
        }
        foreach (var peer in peers.Where(ship => ship.Heading is not null))
        {
            ApplyCourse(peer);
        }
    }

    private void ApplyCourse(ShipContact ship)
    {
        long id = ship.Pilot.ShipId;
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

    private void Stop()
    {
        _direction = null;
        _approachPoint = null;
        _velocity = new(0, 0, 0);
        var position = Pilot.Position!;
        Update("Stop", Pilot.ShipId);
        Update("SetBallPosition", Pilot.ShipId, position.X, position.Y, position.Z);
        Update("SetBallVelocity", Pilot.ShipId, 0d, 0d, 0d);
    }

    private void Update(string method, params object?[] args) =>
        Updates.Add(new object?[] { Stamp, new object?[] { method, args } });

    public object? Call(string service, string method, object?[] arguments)
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
        switch (method)
        {
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
                Stop();
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
