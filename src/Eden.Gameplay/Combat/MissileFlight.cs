using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Combat;

public sealed record MissileDrive(double Mass, float Radius, float MaxVelocity, float Agility, double LifetimeMilliseconds)
{
    public double InertiaSeconds => Mass * Agility / MissileFlightRules.InertiaMassScale;
    public double LifetimeSeconds => LifetimeMilliseconds / TimeSpan.MillisecondsPerSecond;
    public double NominalRange => MaxVelocity * LifetimeSeconds;
}

public sealed record MissileLaunchPlatform(SpacePosition Position, SpacePosition Velocity, float MaxVelocity, SpacePosition RearDirection);

public sealed record MissileTarget(SpacePosition Position, SpacePosition Velocity, float Radius);

public enum MissileFlightStatus
{
    Flying,
    Impact,
    Expired,
    Attached,
}

public sealed record MissileFlight(
    MissileDrive Drive,
    SpacePosition Position,
    SpacePosition Velocity,
    SpacePosition Goto,
    double ElapsedSeconds = 0,
    bool Orphaned = false,
    MissileFlightStatus Status = MissileFlightStatus.Flying
);

public sealed record MissileContact(
    long Id,
    int TypeId,
    int GroupId,
    int SystemId,
    long PilotId,
    long SourceShipId,
    IReadOnlyList<long> LaunchModules,
    long TargetId,
    float TargetRadius,
    int CorporationId,
    int LaunchStamp,
    MissileFlight Flight
);
