using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Exploration;

public sealed record WormholeContact(int DestinationSystem, int DestinationClass, double MaximumShipMass);

public sealed record WormholeJump(StaticSpaceObject Exit, FlightSystem System, int DestinationClass);

public sealed record WormholePolarization(long EndsAt, int Seconds);

public interface IWormholeTravel
{
    IReadOnlyList<CombatAnomaly> Signatures(int systemId);
    WormholeJump Prepare(long pilotId, long shipId, long entranceId);
    DockedPilot Transfer(long pilotId, long shipId, long entranceId, long exitId);
    WormholePolarization? Polarization(long pilotId, int systemId, long entranceId);
}

public static class WormholeRules
{
    public const int Group = 988;
    public const int StrengthAttribute = 1908;
    public const double ActivationRange = 5000;
    public const int PolarizationSeconds = 300;
    public const long FirstEndpointId = 8_800_000_000_000;
    public const long LastEndpointId = FirstEndpointId + 5;
    public const int FirstWormholeSystem = 31000000;
    public const int LastWormholeSystem = 31999999;

    public static bool IsWormholeSystem(int system) => system is >= FirstWormholeSystem and <= LastWormholeSystem;

    public static bool IsEndpoint(long id) => id is >= FirstEndpointId and <= LastEndpointId;
}
