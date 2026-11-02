using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Flight;

internal sealed class FlightSession(IShipPositionStore world, DockedPilot pilot, TimeProvider clock)
{
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
        double tau = Ballpark.Mass * Ballpark.Agility / 1_000_000d;
        double decay = Math.Exp(-seconds / tau);
        double cruise = Ballpark.MaxSpeed * _speedFraction;
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
        SetPosition(next);
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
        int stamp = Stamp;
        Updates.Add(new object?[] { stamp, new object?[] { "SetState", new object?[] { Ballpark.Snapshot(Pilot, stamp) } } });
        if (_direction is { } heading)
        {
            Update("SetBallVelocity", Pilot.ShipId, _velocity.X, _velocity.Y, _velocity.Z);
            if (_approachPoint is not null)
            {
                Update("FollowBall", Pilot.ShipId, Pilot.StationId, _followRange);
            }
            else
            {
                Update("GotoDirection", Pilot.ShipId, heading.X, heading.Y, heading.Z);
            }
            Update("SetSpeedFraction", Pilot.ShipId, _speedFraction);
        }
    }

    private void Update(string method, params object?[] args) =>
        Updates.Add(new object?[] { Stamp, new object?[] { method, args } });

    public object? Call(string service, string method, object?[] arguments)
    {
        if (service == "ship" && method == "Undock" && Pilot.Position is null &&
            arguments is [long id, ..] && id == Pilot.ShipId)
        {
            SetPosition(new(Pilot.X + Ballpark.StationRadius + 500, Pilot.Y, Pilot.Z));
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
                    double radius = Ballpark.StationRadius + Ballpark.ShipRadius + standOff;
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
                _velocity = new(0, 0, 0);
                position = Pilot.Position!;
                Update("Stop", Pilot.ShipId);
                Update("SetBallPosition", Pilot.ShipId, position.X, position.Y, position.Z);
                Update("SetBallVelocity", Pilot.ShipId, 0d, 0d, 0d);
                return null;
            case "CmdDock" when arguments is [long station, long ship] && station == Pilot.StationId && ship == Pilot.ShipId:
                Checkpoint();
                position = Pilot.Position!;
                if (DistanceToStation(position) > Ballpark.StationRadius + Ballpark.ShipRadius + 2500)
                {
                    return PilotCallRouter.Unsupported;
                }
                SetPosition(null);
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
