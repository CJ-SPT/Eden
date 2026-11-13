using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Exploration;

public interface IDedComplexTravel
{
    MissionGate Gate(long pilotId, long gateId, long now);
    MissionGateActivation ActivateGate(long pilotId, long gateId, SpacePosition destination, long now);
    SpacePosition WarpDestination(int systemId, SpacePosition origin, SpacePosition destination, long now, long pilotId = 0);
}

public sealed record DedEscalation(
    long InstanceId,
    int SystemId,
    int DungeonId,
    int NameId,
    int DescriptionId,
    int FactionId,
    int Difficulty,
    int SourceDungeonId,
    long ExpiresAt
);

public interface IDedEscalations
{
    IReadOnlyList<DedEscalation> Journal(long pilotId, long now);
    CombatAnomaly Find(long pilotId, int systemId, long instanceId, long now);
    IReadOnlyList<CombatAnomaly> Sites(long pilotId, int systemId, long now);
    void Arrive(long pilotId, int systemId, SpacePosition position, long now);
}

public static class DedComplexRules
{
    // Reserved slot in the existing shared combat site table and scanner sequence.
    public const int SiteSlot = 100;
    public const int EscalationSlot = 101;
    public const int EscalationArchetype = 31;
    public const int RoomsPerSite = 63;
    public const int HighSecurity = 1;
    public const int LowSecurity = 2;
    public const int NullSecurity = 4;
    public const int DroneRegionFaction = 500021;
    public const int NullDroneHive = 2413;
    public const long FirstGateId = 7_700_000_000_000;
    public const long GatesPerSite = 100;
    public const long GateEndId = 7_800_000_000_000;

    public static bool IsGate(long id) => id is >= FirstGateId and < GateEndId;

    public static bool IsSite(int slot) => slot is SiteSlot or EscalationSlot;

    public static int SecurityBand(double security) =>
        security >= 0.45 ? HighSecurity
        : security > 0 ? LowSecurity
        : NullSecurity;
}
