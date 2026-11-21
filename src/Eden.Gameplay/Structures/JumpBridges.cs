using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Structures;

public sealed record JumpBridgeEndpoint(long Id, int SystemId, int DestinationSystemId, int OwnerId, string Name);

public sealed record JumpBridgeLink(JumpBridgeEndpoint First, JumpBridgeEndpoint Second);

public sealed record JumpBridgeNetwork(IReadOnlyList<JumpBridgeLink> Links, IReadOnlyList<long> Allowed, IReadOnlyList<long> Denied);

public sealed record JumpBridgeInfo(bool ValidShip, double Capacitor, long AsOf);

public sealed record JumpBridgeContact(int DestinationSystemId, bool Active, long Revision);

public sealed record JumpBridgeDeparture(StaticSpaceObject Exit, FlightSystem System);

public interface IPilotJumpBridges
{
    JumpBridgeNetwork Network(long pilotId);
    IReadOnlyList<JumpBridgeEndpoint> Nearby(int systemId);
    int? Destination(long structureId);
    JumpBridgeEndpoint? Linked(long structureId);
    JumpBridgeInfo? Info(long pilotId, long shipId, long structureId);
    JumpBridgeDeparture CheckJump(long pilotId, long shipId, long sourceId);
    DockedPilot Transfer(long pilotId, long shipId, long sourceId, long exitId);
}

public static class JumpBridgeRules
{
    // Build 3662168 24.01 jumpbridge/common/zone_modifier_util.pyj and the Upwell catalog.
    public const int Ansiblex = 35841;
    public const int Service = 35913;
    public const int SovereigntyUpgrade = 81621;
    public const int Group = 1408;
    public const int StargateGroup = 10;
    public const double MinimumStargateSeparation = 100_000_000;

    public static int Zone(double? lightYears) =>
        lightYears switch
        {
            <= 5 => 0,
            <= 10 => 1,
            <= 15 => 2,
            <= 20 => 3,
            _ => 4,
        };
}
