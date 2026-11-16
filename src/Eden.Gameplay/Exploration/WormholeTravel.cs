using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Exploration;

public sealed record WormholeContact(int DestinationSystem, int DestinationClass, double MaximumShipMass, int Age = 0, double Size = 1);

public sealed record WormholeJump(StaticSpaceObject Exit, FlightSystem System, int DestinationClass);

public sealed record WormholePolarization(long EndsAt, int Seconds);

public interface IWormholeTravel
{
    IReadOnlyList<CombatAnomaly> Signatures(int systemId);
    WormholeJump Prepare(long pilotId, long shipId, long entranceId);
    DockedPilot Transfer(long pilotId, long shipId, long entranceId, long exitId);
    WormholePolarization? Polarization(long pilotId, int systemId, long entranceId);
    void Activate(int systemId, long entranceId);
}

public static class WormholeRules
{
    public const int Group = 988;
    public const int StrengthAttribute = 1908;
    public const double ActivationRange = 5000;
    public const int PolarizationSeconds = 300;
    public const long FirstEndpointId = 8_800_000_000_000;
    public const long LastEndpointId = FirstEndpointId + 99_999_999;
    public const int ReplacementSeconds = 60;
    public const int ExitType = 30831; // Build 3662168: K162.
    public const int TargetClassAttribute = 1381;
    public const int LifetimeAttribute = 1382; // Minutes.
    public const int TotalMassAttribute = 1383;
    public const int RegenerationAttribute = 1384; // Kilograms per day.
    public const int JumpMassAttribute = 1385;

    public static double Regenerate(double remaining, double total, double daily, long previous, long now) =>
        remaining <= 0 ? 0 : Math.Min(total, remaining + daily * Math.Max(0, now - previous) / TimeSpan.TicksPerDay);

    public static int Age(long expiresAt, long now) =>
        expiresAt == 0 || expiresAt - now > TimeSpan.TicksPerDay ? 0
        : expiresAt - now > 4 * TimeSpan.TicksPerHour ? 1
        : expiresAt - now > TimeSpan.TicksPerHour ? 2
        : 3;

    // The native client uses visual size bands, not the exact remaining mass fraction.
    public static double Size(double remaining, double total) =>
        remaining > total * .5 ? 1
        : remaining > total * .1 ? .7
        : .4;

    public static int MassClass(double mass) =>
        mass <= 5_000_000 ? 1
        : mass <= 62_000_000 ? 2
        : mass <= 1_000_000_000 ? 3
        : 4;

    public const int FirstWormholeSystem = 31000000;
    public const int LastWormholeSystem = 31999999;

    public static bool IsWormholeSystem(int system) => system is >= FirstWormholeSystem and <= LastWormholeSystem;

    public static bool IsEndpoint(long id) => id is >= FirstEndpointId and <= LastEndpointId;
}
