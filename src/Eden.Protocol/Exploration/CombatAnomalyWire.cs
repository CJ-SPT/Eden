using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
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
                ["position"] = new object[] { Site.Position.X, Site.Position.Y, Site.Position.Z },
                ["targetID"] = Site.TargetId,
                ["difficulty"] = Site.Difficulty,
                ["dungeonID"] = Site.DungeonId,
                ["archetypeID"] = CombatAnomalyRules.CombatSiteArchetypeId,
                ["dungeonNameID"] = Site.NameId,
                ["factionID"] = NpcEncounterDefinitions.GuristasFaction,
                ["scanStrengthAttribute"] = null,
                ["allowedTypes"] = null,
            }
        );
}

internal sealed record AnomalyMap(IReadOnlyList<CombatAnomaly> Sites) : BlueModel
{
    public override object ToBlue() => Sites.ToDictionary(s => s.Id, s => (object?)new AnomalySiteInfo(s));
}

internal sealed record SignalTrackerState(IReadOnlyList<CombatAnomaly> Sites) : BlueModel
{
    // Build 3662168 unpacks anomalies, signatures, static sites, structures.
    public override object ToBlue() =>
        new object[]
        {
            new AnomalyMap(Sites),
            new Dictionary<long, object?>(),
            new Dictionary<long, object?>(),
            new Dictionary<long, object?>(),
        };
}

internal sealed record SignalTrackerFullState(int SystemId, IReadOnlyList<CombatAnomaly> Sites)
    : PilotNotification("OnSignalTrackerFullState")
{
    public override object?[] Arguments => [SystemId, new SignalTrackerState(Sites), true];
}

internal sealed record SignalTrackerAnomalyUpdate(int SystemId, IReadOnlyList<CombatAnomaly> Added, IReadOnlyList<long> Removed)
    : PilotNotification("OnSignalTrackerAnomalyUpdate")
{
    // Python removal sets use the codec's explicit constructor representation.
    public override object?[] Arguments =>
        [SystemId, new AnomalyMap(Added), new BlueConstructor("__builtin__.set", [Removed.Select(id => (object?)id).ToList()])];
}
