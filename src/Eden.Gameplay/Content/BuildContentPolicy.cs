using System.Text.Json;
using System.Text.Json.Serialization;
using Eden.Gameplay.Combat;

namespace Eden.Gameplay.Content;

// Enabled content is build-specific data. Capability checks still belong to their typed feature rules.
public static class BuildContentPolicy
{
    public const int PinnedBuild = 3662168;
    private static readonly Lazy<CatalogContentSupport> s_current = new(Load);
    public static CatalogContentSupport Current => s_current.Value;

    private static CatalogContentSupport Load()
    {
        using var stream =
            typeof(BuildContentPolicy).Assembly.GetManifestResourceStream("Eden.Gameplay.ContentSupport.json")
            ?? throw new InvalidDataException("Missing build-specific content policy.");
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new JsonStringEnumConverter());
        var policy = JsonSerializer.Deserialize<CatalogContentSupport>(stream, options);
        if (
            policy?.Build != PinnedBuild
            || policy.Manufacturing?.HullTypes is null
            || policy.Manufacturing.UtilityTypes is null
            || policy.Insurance?.MaterialValuedHullTypes is null
            || policy.Hulls is null
            || policy.Hulls.Cohorts.IsDefaultOrEmpty
            || policy.Hulls.Cohorts.Any(cohort => cohort is null || cohort.Classes is null || cohort.Types is null)
            || policy.DroneHullTypes is null
            || policy.Npcs?.ExcludedTypes is null
            || policy.Npcs.Guards is null
            || policy.Npcs.PirateRegions is null
            || policy.Npcs.Loot is null
            || policy.StartingEquipment is null
        )
        {
            throw new InvalidDataException("Incomplete build-specific content policy.");
        }
        var identifiers = policy
            .Manufacturing.HullTypes.Concat(policy.Manufacturing.UtilityTypes)
            .Concat(policy.Insurance.MaterialValuedHullTypes)
            .Concat(policy.Hulls.Cohorts.SelectMany(cohort => cohort.Classes.Concat(cohort.Types)))
            .Concat(policy.DroneHullTypes)
            .Concat(policy.Npcs.ExcludedTypes)
            .Concat(policy.Npcs.PirateRegions.Keys);
        if (
            identifiers.Any(id => id <= 0)
            || policy.Hulls.Cohorts.Any(cohort =>
                cohort.TechLevel <= 0 || !Enum.IsDefined(cohort.Cohort) || cohort.Classes.Count + cohort.Types.Count == 0
            )
            || policy.Hulls.Cohorts.Select(cohort => (cohort.TechLevel, cohort.Cohort)).Distinct().Count() != policy.Hulls.Cohorts.Length
            || policy.Npcs.FallbackLootModule <= 0
            || policy.StartingEquipment.MinerType <= 0
            || policy.StartingEquipment.WeaponType <= 0
            || policy.Npcs.Guards.Any(pair =>
                !Enum.IsDefined((NpcEmpireFaction)pair.Key)
                || pair.Value is null
                || !Enum.IsDefined(pair.Value.Frigate)
                || !Enum.IsDefined(pair.Value.Cruiser)
                || !Enum.IsDefined(pair.Value.Customs)
            )
            || policy.Npcs.PirateRegions.Values.Any(faction => !Enum.IsDefined(faction))
            || policy.Npcs.Loot.Any(pair =>
                !Enum.IsDefined(pair.Key)
                || pair.Value is null
                || pair.Value.Ammunition.IsDefault
                || pair.Value.Repair.IsDefault
                || pair.Value.Ammunition.Length != NpcLootRules.SizeCount
                || pair.Value.Repair.Length != NpcLootRules.SizeCount
                || pair.Value.Ammunition.Concat(pair.Value.Repair).Append(pair.Value.Support).Any(id => id <= 0)
                || pair.Value.AmmunitionUnitsPerSize <= 0
            )
        )
        {
            throw new InvalidDataException("Invalid build-specific content identifiers or loot vectors.");
        }
        return policy;
    }
}
