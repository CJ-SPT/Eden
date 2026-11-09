using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Exploration;

internal static class ProbeScanningWire
{
    internal const int IdleState = 1;
    private const int ScanningState = 4;
    private const int CombatStrengthAttribute = 1136;

    internal static object[] Position(SpacePosition p) => [p.X, p.Y, p.Z];

    internal static SpacePosition CoarsePosition(SpacePosition p) =>
        new(
            Math.Round(p.X / ProbeScanRules.AstronomicalUnit) * ProbeScanRules.AstronomicalUnit,
            Math.Round(p.Y / ProbeScanRules.AstronomicalUnit) * ProbeScanRules.AstronomicalUnit,
            Math.Round(p.Z / ProbeScanRules.AstronomicalUnit) * ProbeScanRules.AstronomicalUnit
        );

    internal static ProbePlacement Placement(long id, object? value)
    {
        var fields = value switch
        {
            BlueInstance { Name: "util.KeyVal" or "utillib.KeyVal", State: Dictionary<string, object?> state } => state,
            Dictionary<string, object?> state => state,
            _ => throw new InvalidDataException("Expected a native probe placement."),
        };
        if (
            fields.GetValueOrDefault("probeID") is not long probe
            || probe != id
            || fields.GetValueOrDefault("rangeStep") is not long step
            || step is < 1 or > ProbeScanRules.RangeSteps
            || fields.GetValueOrDefault("destination") is not object?[] { Length: 3 } point
            || point.Any(v => v is not (long or double))
        )
        {
            throw new InvalidDataException("Invalid probe identifier, destination or range step.");
        }
        return new(id, new(Convert.ToDouble(point[0]), Convert.ToDouble(point[1]), Convert.ToDouble(point[2])), (int)step);
    }

    internal static BlueInstance Probe(ScanProbe probe, int state = IdleState) =>
        new(
            "util.KeyVal",
            new Dictionary<string, object?>
            {
                ["probeID"] = probe.Id,
                ["typeID"] = probe.TypeId,
                ["pos"] = Position(probe.Position),
                ["destination"] = Position(probe.Position),
                ["expiry"] = probe.ExpiresAt,
                ["state"] = state,
                ["scanBonuses"] = new Dictionary<string, object?>
                {
                    ["strength"] = new Dictionary<string, object?>
                    {
                        ["ship"] = probe.Profile.Bonuses?.ShipStrength ?? 0,
                        ["modules"] = probe.Profile.Bonuses?.ModuleStrength ?? 0,
                    },
                    ["deviation"] = new Dictionary<string, object?>
                    {
                        ["ship"] = probe.Profile.Bonuses?.ShipDeviation ?? 0,
                        ["modules"] = probe.Profile.Bonuses?.ModuleDeviation ?? 0,
                    },
                },
            }
        );

    internal static BlueInstance Result(ProbeScanResult result)
    {
        bool group = result.Certainty >= ProbeScanRules.GroupThreshold;
        bool name = result.Certainty >= ProbeScanRules.NameThreshold;
        bool perfect = result.Certainty >= 1;
        return new(
            "util.KeyVal",
            new Dictionary<string, object?>
            {
                ["id"] = result.Site.TargetId,
                ["scanGroupID"] = ProbeScanRules.SignatureScanGroup,
                ["groupID"] = ProbeScanRules.SignatureGroup,
                ["typeID"] = name ? ProbeScanRules.SignatureType : null,
                ["strengthAttributeID"] = group ? CombatStrengthAttribute : null,
                ["dungeonID"] = name ? result.Site.DungeonId : null,
                ["dungeonNameID"] = name ? result.Site.NameId : null,
                ["archetypeID"] = group ? CombatAnomalyRules.CombatSiteArchetypeId : null,
                ["factionID"] = name ? result.Site.FactionId : null,
                ["certainty"] = result.Certainty,
                ["pos"] = Position(result.Position),
                ["data"] = perfect ? Position(result.Position) : (object)result.Deviation,
                ["itemID"] = null,
                ["difficulty"] = name ? result.Site.Difficulty : null,
            }
        );
    }

    internal static Dictionary<long, object?> Scanning(ProbeScan scan) =>
        scan.Probes.ToDictionary(p => p.Id, p => (object?)Probe(p, ScanningState));
}

internal sealed record SignatureMap(IReadOnlyList<CombatAnomaly> Sites) : BlueModel
{
    public override object ToBlue() =>
        Sites.ToDictionary(
            s => s.Id,
            s =>
                (object?)
                    new BlueInstance(
                        "util.KeyVal",
                        new Dictionary<string, object?>
                        {
                            ["position"] = ProbeScanningWire.Position(ProbeScanningWire.CoarsePosition(s.Position)),
                            ["targetID"] = s.TargetId,
                            ["difficulty"] = null,
                            ["dungeonID"] = null,
                            ["archetypeID"] = null,
                            ["deviation"] = ProbeScanRules.AstronomicalUnit,
                        }
                    )
        );
}

internal sealed record SignalTrackerSignatureUpdate(int SystemId, IReadOnlyList<CombatAnomaly> Added, IReadOnlyList<long> Removed)
    : PilotNotification("OnSignalTrackerSignatureUpdate")
{
    public override object?[] Arguments =>
        [SystemId, new SignatureMap(Added), new BlueConstructor("__builtin__.set", [Removed.Select(id => (object?)id).ToList()])];
}

internal sealed record NewScanProbe(ScanProbe Probe) : PilotNotification("OnNewProbe")
{
    public override object?[] Arguments => [ProbeScanningWire.Probe(Probe)];
}

internal sealed record RemoveScanProbe(long Id) : PilotNotification("OnRemoveProbe")
{
    public override object?[] Arguments => [Id];
}

internal sealed record SystemScanStarted(ProbeScan Scan) : PilotNotification("OnSystemScanStarted")
{
    public override object?[] Arguments =>
        [Scan.StartedAt, (Scan.ReadyAt - Scan.StartedAt) / (double)TimeSpan.TicksPerMillisecond, ProbeScanningWire.Scanning(Scan)];
}

internal sealed record SystemScanStopped(ProbeScan Scan, IReadOnlyList<ProbeScanResult> Results) : PilotNotification("OnSystemScanStopped")
{
    public override object?[] Arguments =>
        [
            Scan.Probes.Select(p => (object?)p.Id).ToList(),
            Results.Select(r => (object?)ProbeScanningWire.Result(r)).ToList(),
            Scan.Results.ExceptBy(Results.Select(r => r.Site.Id), r => r.Site.Id).Select(r => (object?)r.Site.TargetId).ToList(),
        ];
}

internal sealed record RecoveredProbeIds(IReadOnlyList<long> Ids) : BlueModel
{
    public override object ToBlue() => Ids.Select(id => (object?)id).ToList();
}
