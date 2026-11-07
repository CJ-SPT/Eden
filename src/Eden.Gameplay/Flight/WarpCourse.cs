using Eden.Gameplay.Docking;

namespace Eden.Gameplay.Flight;

public sealed class WarpCourse
{
    private readonly SpacePosition _origin;
    private readonly SpacePosition _direction;
    private readonly double _entrySpeed;
    private readonly double _acceleration;
    private readonly double _braking;
    private readonly double _peak;
    private readonly double _accelerationTime;
    private readonly double _cruiseTime;

    public WarpCourse(SpacePosition origin, SpacePosition destination, int startStamp, FlightProperties hull, double entrySpeed)
    {
        _origin = origin;
        Destination = destination;
        StartStamp = startStamp;
        Distance = origin.DistanceTo(destination);
        _direction = (destination - origin).Unit;
        _entrySpeed = entrySpeed;
        WireFactor = Math.Max(1, (int)Math.Round(hull.WarpSpeed * 1000));
        _acceleration = WireFactor / 1000.0;
        _braking = Math.Min(WireFactor / 3000.0, 2);
        _peak = Math.Min(_acceleration * 149_597_870_700, (Distance + 1) / (1 / _acceleration + 1 / _braking));
        _accelerationTime = Math.Log(_peak / _acceleration) / _acceleration;
        _cruiseTime = Math.Max(0, (Distance + 1) / _peak - 1 / _acceleration - 1 / _braking);
    }

    public SpacePosition Destination { get; }
    public double Distance { get; }
    public int WireFactor { get; }
    public int StartStamp { get; }

    public double DurationSeconds(double exitSpeed) =>
        Math.Max(0, _accelerationTime + _cruiseTime + Math.Log(_peak / exitSpeed) / _braking);

    public (SpacePosition Position, SpacePosition Velocity, bool Exited) Sample(int stamp, double exitSpeed)
    {
        double time = stamp - StartStamp;
        double distance;
        double speed;
        bool exited = false;
        if (time < _accelerationTime)
        {
            distance = Math.Exp(_acceleration * time);
            speed = Math.Max(_entrySpeed, _acceleration * distance);
        }
        else if (time < _accelerationTime + _cruiseTime)
        {
            speed = _peak;
            distance = _peak / _acceleration + speed * (time - _accelerationTime);
        }
        else
        {
            speed = _peak * Math.Exp(-_braking * (time - _accelerationTime - _cruiseTime));
            distance = Distance + 1 - speed / _braking;
            exited = speed < exitSpeed;
        }
        return (_origin + _direction * distance, _direction * speed, exited);
    }
}
