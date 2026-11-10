using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Exploration;

internal sealed record AnomalySiteInfo(CombatAnomaly Site) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "util.KeyVal",
            new Dictionary<string, object?>
            {
                ["instanceID"] = Site.Id,
                ["siteID"] = Site.Id,
                ["position"] = new object[] { Site.Position.X, Site.Position.Y, Site.Position.Z },
                ["targetID"] = Site.TargetId,
                ["difficulty"] = Site.Difficulty,
                ["dungeonID"] = Site.DungeonId,
                ["archetypeID"] = Site.Mining == MiningFamily.Ice ? MiningSiteRules.IceArchetype : CombatAnomalyRules.CombatSiteArchetypeId,
                ["dungeonNameID"] = Site.NameId,
                ["factionID"] = Site.FactionId,
                ["scanStrengthAttribute"] = null,
                ["allowedTypes"] = null,
            }
        );
}

internal sealed record AnomalyMap(IReadOnlyList<CombatAnomaly> Sites) : BlueModel
{
    public override object ToBlue() => Sites.ToDictionary(s => s.Id, s => (object?)new AnomalySiteInfo(s));
}

internal sealed record SignalTrackerState(IReadOnlyList<CombatAnomaly> Sites, IReadOnlyList<CombatAnomaly>? Signatures = null) : BlueModel
{
    // Build 3662168 unpacks anomalies, signatures, static sites, structures.
    public override object ToBlue() =>
        new object[]
        {
            new AnomalyMap(Sites),
            new SignatureMap(Signatures ?? []),
            new Dictionary<long, object?>(),
            new Dictionary<long, object?>(),
        };
}

internal sealed record SignalTrackerFullState(
    int SystemId,
    IReadOnlyList<CombatAnomaly> Sites,
    IReadOnlyList<CombatAnomaly>? Signatures = null
) : PilotNotification("OnSignalTrackerFullState")
{
    public override object?[] Arguments => [SystemId, new SignalTrackerState(Sites, Signatures), true];
}

internal sealed record SignalTrackerAnomalyUpdate(int SystemId, IReadOnlyList<CombatAnomaly> Added, IReadOnlyList<long> Removed)
    : PilotNotification("OnSignalTrackerAnomalyUpdate")
{
    // Python removal sets use the codec's explicit constructor representation.
    public override object?[] Arguments =>
        [SystemId, new AnomalyMap(Added), new BlueConstructor("__builtin__.set", [Removed.Select(id => (object?)id).ToList()])];
}

internal sealed record AnomalyDirectory(IReadOnlyList<AnomalySystemSites> Systems) : BlueModel
{
    public override object ToBlue() =>
        Systems.ToDictionary(
            s => (long)s.SystemId,
            s => (object?)new BlueList<AnomalySiteInfo>(s.Sites.Select(site => new AnomalySiteInfo(site)).ToArray())
        );
}

internal sealed record AnomalyCounts(IReadOnlyList<AnomalySystemSites> Systems) : BlueModel
{
    public override object ToBlue() => Systems.ToDictionary(s => (long)s.SystemId, s => (object?)s.Sites.Count);
}
