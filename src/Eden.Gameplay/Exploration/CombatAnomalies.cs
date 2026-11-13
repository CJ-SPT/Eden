using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Exploration;

public sealed record CombatAnomaly(
    long Id,
    string TargetId,
    int SystemId,
    int DungeonId,
    int NameId,
    int Difficulty,
    SpacePosition Position,
    bool Started,
    IReadOnlyList<StaticSpaceObject> Objects,
    int FactionId = 500010,
    bool IsSignature = false,
    ExplorationKind? Exploration = null,
    Eden.Gameplay.Mining.MiningFamily? Mining = null,
    bool Wormhole = false,
    bool Ded = false,
    long? GhostCountdownEnd = null,
    int GhostRoomId = 0
);

public sealed record AnomalySystemSites(int SystemId, IReadOnlyList<CombatAnomaly> Sites);

public interface ICombatAnomalies
{
    IReadOnlyList<CombatAnomaly> List(int systemId, long now);
    IReadOnlyList<CombatAnomaly> Signatures(int systemId, long now) => [];
    void Arrive(int systemId, SpacePosition position, long now);
    void ArriveUncloaked(int systemId, SpacePosition position, long now) { }
    IReadOnlyList<AnomalySystemSites> Directory(long now);
}

public static class CombatAnomalyRules
{
    public const int CombatSiteArchetypeId = 24;
    public const int CombatStrengthAttribute = 1136;
    public const double PlacementOffset = 10_000_000_000;
    public const int PithiArrogator = 16981;
    public const int PithiImputor = 16994;
    public const int PithiInfiltrator = 16996;
    public const int PithiInvader = 16997;
    public const int SystemId = 30000144;
    public const int DungeonId = 2134;
    public const int AnomalyTypeId = 28356;
    public const int TelescopeTypeId = 70824;
    public const int ReplacementSeconds = 600;
    public const double ArrivalRange = 100_000;
    public const long FirstSiteId = 7_000_000_000_000;
    public const long FirstObjectId = 7_100_000_000_000;
    public const long FirstNpcId = 7_200_000_000_000;

    // Eden reconstruction of the Observation Post report. Group IDs in the client
    // have no exported roster. Fix each roll to the persisted site and wave.
    public static IReadOnlyList<int> Wave(long instance, int wave)
    {
        if (wave is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(wave));
        }
        int count = (wave < 2 ? 1 : 2) + (int)((instance + wave) % 2);
        int first = wave % 2 == 0 ? PithiArrogator : PithiInfiltrator;
        int second = wave % 2 == 0 ? PithiImputor : PithiInvader;
        return Enumerable.Range(0, count).Select(slot => (instance + slot + wave) % 2 == 0 ? first : second).ToArray();
    }
}
