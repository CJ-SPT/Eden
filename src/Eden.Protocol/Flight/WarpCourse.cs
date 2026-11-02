using Eden.Protocol.Docking;

namespace Eden.Protocol.Flight;

// Destiny measures warp speed in AU/s and uses exponential acceleration and braking.
internal sealed class WarpCourse
{
    private readonly SpacePosition _origin;
    private readonly SpacePosition _direction;
    private readonly double _startTime;
    private readonly double _acceleration;
    private readonly double _braking;
    private readonly double _peak;
    private readonly double _distance;
    private readonly double _accelerationTime;
    private readonly double _cruiseTime;
    private readonly double _exitSpeed;

    public WarpCourse(SpacePosition origin, SpacePosition landing, double startTime, FlightProperties hull)
    {
        _origin = origin;
        _startTime = startTime;
        _acceleration = hull.WarpSpeed;
        _braking = Math.Min(hull.WarpSpeed / 3, 2);
        _exitSpeed = Math.Min(hull.MaxSpeed / 2, 100);
        double length = origin.DistanceTo(landing);
        _direction = new((landing.X - origin.X) / length, (landing.Y - origin.Y) / length, (landing.Z - origin.Z) / length);
        // Exit warp at sublight speed and coast into the landing lane under normal hull drag.
        _distance = length - _exitSpeed * (hull.InertiaTime - 1 / _braking) - 1;
        Destination = AtDistance(_distance);
        WireFactor = (int)Math.Round(hull.WarpSpeed * 1000);
        _peak = Math.Min(hull.WarpSpeed * 149_597_870_700, (_distance + 1) / (1 / _acceleration + 1 / _braking));
        _accelerationTime = Math.Log(_peak / _acceleration) / _acceleration;
        _cruiseTime = Math.Max(0, (_distance + 1) / _peak - 1 / _acceleration - 1 / _braking);
        ExitTime = startTime + _accelerationTime + _cruiseTime + Math.Log(_peak / _exitSpeed) / _braking;
    }

    public SpacePosition Destination { get; }
    public int WireFactor { get; }
    public double ExitTime { get; }
    public SpacePosition ExitVelocity => new(_direction.X * _exitSpeed, _direction.Y * _exitSpeed, _direction.Z * _exitSpeed);

    public SpacePosition Sample(double now)
    {
        double time = Math.Min(now, ExitTime) - _startTime;
        if (time <= 0)
        {
            return _origin;
        }
        if (time < _accelerationTime)
        {
            return AtDistance(Math.Exp(_acceleration * time));
        }
        double brakingTime = time - _accelerationTime - _cruiseTime;
        if (brakingTime < 0)
        {
            return AtDistance(_peak / _acceleration + _peak * (time - _accelerationTime));
        }
        double speed = _peak * Math.Exp(-_braking * brakingTime);
        return AtDistance(_distance - speed / _braking + 1);
    }

    private SpacePosition AtDistance(double distance) => new(_origin.X + _direction.X * distance,
        _origin.Y + _direction.Y * distance, _origin.Z + _direction.Z * distance);
}
