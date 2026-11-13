using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Exploration;

public interface IDedComplexTravel
{
    MissionGate Gate(long pilotId, long gateId, long now);
    MissionGateActivation ActivateGate(long pilotId, long gateId, SpacePosition destination, long now);
    SpacePosition WarpDestination(int systemId, SpacePosition origin, SpacePosition destination, long now);
}

public static class DedComplexRules
{
    // Reserved slot in the existing shared combat site table and scanner sequence.
    public const int SiteSlot = 100;
    public const long FirstGateId = 7_700_000_000_000;
    public const long GatesPerSite = 100;
    public const long GateEndId = 7_800_000_000_000;

    public static bool IsGate(long id) => id is >= FirstGateId and < GateEndId;
}
