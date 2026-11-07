using System.Text.Json;
using System.Text.Json.Serialization;
using Eden.Gameplay.Content;
using Eden.Gameplay.Ships;

namespace Eden.World.Fittings;

internal sealed record ShipContentRoadmap(
    int Build,
    IReadOnlyDictionary<ShipContentMilestone, int[]> MilestoneGroups,
    int[] ExternalGroups,
    int[] SpecialGroups,
    int[] UnverifiedGroups,
    int[] NonfunctionalTypes
)
{
    private static readonly Lazy<ShipContentRoadmap> s_current = new(Load);
    internal static ShipContentRoadmap Current => s_current.Value;

    internal ShipContentMilestone? ForGroup(int group) =>
        MilestoneGroups.Where(pair => pair.Value.Contains(group)).Select(pair => (ShipContentMilestone?)pair.Key).SingleOrDefault();

    internal static ShipContentMilestone ForRole(HullCapability role) =>
        role switch
        {
            HullCapability.CombatDrones => ShipContentMilestone.CombatDrones,
            HullCapability.EnergyWarfare or HullCapability.RemoteRepair => ShipContentMilestone.EnergyAndRemoteSupport,
            HullCapability.Probes => ShipContentMilestone.ProbeScanning,
            HullCapability.Hacking => ShipContentMilestone.Exploration,
            HullCapability.Salvaging or HullCapability.TractorBeams => ShipContentMilestone.InformationAndSalvage,
            HullCapability.IceMining or HullCapability.GasMining or HullCapability.MiningCrystals or HullCapability.DeepCoreMining =>
                ShipContentMilestone.Mining,
            HullCapability.Cloaking or HullCapability.MicroJumpDrives => ShipContentMilestone.CloakingAndMicroJumps,
            HullCapability.CommandBursts
            or HullCapability.FleetAccess
            or HullCapability.FleetRefitting
            or HullCapability.FrigateEscapeRecovery => ShipContentMilestone.FleetServices,
            HullCapability.Interdiction or HullCapability.Smartbombs => ShipContentMilestone.AreaWeaponsAndInterdiction,
            HullCapability.Disintegrators or HullCapability.VortonProjectors or HullCapability.BreacherPods =>
                ShipContentMilestone.WeaponMechanics,
            HullCapability.CynosuralFields => ShipContentMilestone.CapitalTravel,
            HullCapability.CapitalFlexShieldHardening => ShipContentMilestone.CapitalModes,
            HullCapability.IndustrialCores => ShipContentMilestone.IndustrialCommand,
            _ => ShipContentMilestone.StaticCatalogClosure,
        };

    internal static ShipContentMilestone ForHull(HullClass hullClass, int? techLevel) =>
        techLevel == 3 ? ShipContentMilestone.TechThree
        : hullClass is HullClass.Titan or HullClass.Supercarrier or HullClass.CommandCarrier ? ShipContentMilestone.Supercapitals
        : hullClass == HullClass.Carrier ? ShipContentMilestone.Fighters
        : hullClass == HullClass.CapitalIndustrial ? ShipContentMilestone.IndustrialCommand
        : hullClass is HullClass.Dreadnought or HullClass.ForceAuxiliary or HullClass.LancerDreadnought ? ShipContentMilestone.CapitalModes
        : hullClass is HullClass.Capsule or HullClass.JumpFreighter ? ShipContentMilestone.CapitalTravel
        : techLevel == 2 ? ShipContentMilestone.TechTwoSubcapitals
        : ShipContentMilestone.FittingCapabilities;

    private static ShipContentRoadmap Load()
    {
        using var stream = typeof(ShipContentRoadmap).Assembly.GetManifestResourceStream("Eden.World.ShipContentRoadmap.json")!;
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new JsonStringEnumConverter());
        var roadmap =
            JsonSerializer.Deserialize<ShipContentRoadmap>(stream, options)
            ?? throw new InvalidDataException("Missing ship content roadmap.");
        var groups = roadmap
            .MilestoneGroups.Values.SelectMany(groups => groups)
            .Concat(roadmap.ExternalGroups)
            .Concat(roadmap.SpecialGroups)
            .Concat(roadmap.UnverifiedGroups)
            .ToArray();
        if (
            roadmap.Build != BuildContentPolicy.PinnedBuild
            || roadmap.MilestoneGroups.Keys.Any(stage => !Enum.IsDefined(stage))
            || groups.Any(group => group <= 0)
            || groups.Distinct().Count() != groups.Length
            || roadmap.NonfunctionalTypes.Any(type => type <= 0)
        )
        {
            throw new InvalidDataException("Invalid or ambiguous ship content roadmap.");
        }
        return roadmap;
    }
}
