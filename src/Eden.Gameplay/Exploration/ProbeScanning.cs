using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Exploration;

public sealed record ProbeBonuses(double ShipStrength, double ShipDeviation, double ModuleStrength, double ModuleDeviation);

public sealed record ProbeProfile(
    double Strength,
    double BaseRangeAu,
    double RangeFactor,
    double DeviationAu,
    double LifetimeMs,
    ProbeBonuses? Bonuses = null
);

public sealed record ScanProbe(long Id, int TypeId, int SystemId, SpacePosition Position, long ExpiresAt, ProbeProfile Profile);

public sealed record ProbePlacement(long Id, SpacePosition Destination, int RangeStep);

public sealed record ProbeScanResult(CombatAnomaly Site, double Certainty, SpacePosition Position, double Deviation);

public sealed record ProbeScan(
    long ShipId,
    int SystemId,
    long StartedAt,
    long ReadyAt,
    IReadOnlyList<ScanProbe> Probes,
    IReadOnlyList<ProbeScanResult> Results
);

public sealed record MoonSurveyLaunch(
    long Id,
    int TypeId,
    SpacePosition Position,
    long ReadyAt,
    double MaximumRange,
    int? MoonId,
    IReadOnlyList<Structures.MoonOreShare> Composition,
    Magazine Magazine
);

public sealed record ProbeLaunch(IReadOnlyList<ScanProbe> Probes, Magazine Magazine);

public sealed record ProbeRecovery(IReadOnlyList<long> Removed, IReadOnlyList<InventoryChange> Items);

public interface IPilotProbeScanning
{
    IReadOnlyList<ScanProbe> List(long pilotId, int systemId, long now);
    ProbeScan? Pending(long pilotId);
    bool IsLauncher(long pilotId, long moduleId);
    ProbeLaunch Launch(long pilotId, long moduleId, int count, long now);
    bool IsSurveyLauncher(long pilotId, long moduleId);
    MoonSurveyLaunch LaunchSurvey(long pilotId, long moduleId, SpacePosition heading, long now);
    ProbeRecovery Recover(long pilotId, IReadOnlyList<long> ids, long now);
    void Destroy(long pilotId, long id, long now);
    ProbeScan Begin(long pilotId, IReadOnlyList<ProbePlacement> placements, long now);
    IReadOnlyList<ProbeScanResult> Complete(long pilotId, ProbeScan scan, long now);
    bool Resolved(long pilotId, long siteId, int systemId, long now);
}

public static class ProbeScanRules
{
    public const int ScienceSkill = 3402;
    public const int AstrometricsSkill = 3412;
    public const int RangefindingSkill = 25739;
    public const int PinpointingSkill = 25810;
    public const int AcquisitionSkill = 25811;
    public static IReadOnlyList<int> Skills { get; } = [AstrometricsSkill, RangefindingSkill, PinpointingSkill, AcquisitionSkill];
    public const int LauncherGroup = 481;
    public const int ProbeGroup = 479;
    public const int SurveyLauncherGroup = 1226;
    public const int MaximumProbes = 8;
    public const int RangeSteps = 8;
    public const int SignaturesPerSystem = 2;
    public const int MaximumSignatureDifficulty = 2;
    private const int MinimumResolvingProbes = 4;
    private const double StrengthPerDifficulty = 20;
    private const double MinimumGeometry = 0.05;
    private const double BaseGeometryContribution = 0.5;
    private const double UnusableSignal = 0.001;
    public const long FirstProbeId = 7_300_000_000_000;
    public const double AstronomicalUnit = 149_597_870_700;
    public const double MaximumPosition = 250 * AstronomicalUnit;
    public const double GroupThreshold = 0.25;
    public const double NameThreshold = 0.75;
    public const int SignatureGroup = 502;
    public const int SignatureType = 19728;
    public const int SignatureScanGroup = 3;

    public static void Validate(ProbePlacement placement, ProbeProfile profile)
    {
        if (
            placement.RangeStep is < 1 or > RangeSteps
            || !Finite(placement.Destination)
            || placement.Destination.Length > MaximumPosition
            || !double.IsFinite(profile.Strength)
            || profile.Strength <= 0
            || !double.IsFinite(profile.BaseRangeAu)
            || profile.BaseRangeAu <= 0
            || !double.IsFinite(profile.RangeFactor)
            || profile.RangeFactor <= 1
            || !double.IsFinite(profile.DeviationAu)
            || profile.DeviationAu < 0
        )
        {
            throw new InvalidDataException("Invalid probe position, range or scan profile.");
        }
    }

    public static bool Finite(SpacePosition p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);

    // CCP's server-side solver is not exported. This deterministic Eden solver
    // uses overlapping probes, coverage and three-dimensional geometry; repeating
    // an unchanged formation never increases its signal strength or rerolls error.
    public static ProbeScanResult? Scan(CombatAnomaly site, IReadOnlyList<(ScanProbe Probe, ProbePlacement Placement)> probes)
    {
        var covering = probes
            .Select(p =>
            {
                Validate(p.Placement, p.Probe.Profile);
                double scale = Math.Pow(p.Probe.Profile.RangeFactor, p.Placement.RangeStep - 1);
                double radius = p.Probe.Profile.BaseRangeAu * scale * AstronomicalUnit;
                double distance = p.Placement.Destination.DistanceTo(site.Position);
                double strength = p.Probe.Profile.Strength / scale * Math.Max(0, 1 - Math.Pow(distance / radius, 2));
                return (p.Probe, p.Placement, Radius: radius, Strength: strength, Scale: scale);
            })
            .Where(p => p.Strength > 0)
            .OrderByDescending(p => p.Strength)
            .Take(MaximumProbes)
            .ToArray();
        if (covering.Length == 0)
        {
            return null;
        }
        double geometry = 0;
        for (int a = 0; a < covering.Length; a++)
        {
            for (int b = a + 1; b < covering.Length; b++)
            {
                for (int c = b + 1; c < covering.Length; c++)
                {
                    var x = (covering[a].Placement.Destination - site.Position).Unit;
                    var y = (covering[b].Placement.Destination - site.Position).Unit;
                    var z = (covering[c].Placement.Destination - site.Position).Unit;
                    geometry = Math.Max(geometry, Math.Abs(x.Dot(y.Cross(z))));
                }
            }
        }
        double count = Math.Min(1, covering.Length / (double)MinimumResolvingProbes);
        double certainty = Math.Clamp(
            covering.Average(p => p.Strength)
                / (StrengthPerDifficulty * Math.Max(1, site.Difficulty))
                * count
                * (BaseGeometryContribution * (1 + geometry)),
            0,
            1
        );
        if (covering.Length < MinimumResolvingProbes || geometry < MinimumGeometry)
        {
            certainty = Math.Min(certainty, NameThreshold - UnusableSignal);
        }
        double deviation =
            certainty >= 1 ? 0 : covering.Min(p => p.Probe.Profile.DeviationAu * p.Scale) * AstronomicalUnit * (1 - certainty);
        // Stable direction derived from the site identity, unrelated to scan count.
        var direction = new SpacePosition((site.Id % 7) - 3, (site.Id % 11) - 5, (site.Id % 13) - 6).Unit;
        return new(site, certainty, site.Position + direction * deviation, deviation);
    }
}
