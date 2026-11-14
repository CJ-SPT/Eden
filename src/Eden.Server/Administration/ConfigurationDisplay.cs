using System.Text.Json;
using Eden.Gameplay.Configuration;

namespace Eden.Server.Administration;

internal sealed record SettingsComparisonRow(string Label, string Original, string Active, string Pending);

internal sealed record RevisionDifference(string Field, string Before, string After);

internal static class ConfigurationDisplay
{
    private static readonly IReadOnlyDictionary<string, string> s_labels = new Dictionary<string, string>
    {
        ["resources.oreYieldMultiplier"] = "Ore mining yield",
        ["resources.iceYieldMultiplier"] = "Ice harvesting yield",
        ["resources.gasYieldMultiplier"] = "Gas harvesting yield",
        ["miningHolds.generalMultiplier"] = "General mining hold capacity",
        ["miningHolds.iceMultiplier"] = "Ice hold capacity",
        ["miningHolds.gasMultiplier"] = "Gas hold capacity",
        ["rewards.bountyMultiplier"] = "NPC bounty ISK",
        ["rewards.missionIskMultiplier"] = "Mission ISK",
        ["rewards.missionLpMultiplier"] = "Mission loyalty points",
        ["rewards.missionStandingMultiplier"] = "Positive mission standings",
        ["progression.killSkillPointMultiplier"] = "NPC kill skill points",
        ["progression.miningSkillPointMultiplier"] = "Mining skill points",
        ["industry.manufacturingSpeed"] = "Manufacturing speed",
        ["industry.researchSpeed"] = "Research speed",
        ["industry.copyingSpeed"] = "Copying speed",
        ["industry.inventionSpeed"] = "Invention speed",
        ["industry.reprocessingEfficiency"] = "Reprocessing efficiency",
        ["planetaryIndustry.extractionYieldMultiplier"] = "Planetary extraction yield",
        ["planetaryIndustry.factoryOutputMultiplier"] = "Planetary factory output",
        ["planetaryIndustry.cpuMultiplier"] = "Planetary command center CPU",
        ["planetaryIndustry.powergridMultiplier"] = "Planetary command center powergrid",
        ["structures.anchoringSpeed"] = "Structure anchoring speed",
        ["structures.onliningSpeed"] = "Structure core onlining speed",
        ["structures.unanchoringSpeed"] = "Structure decommissioning speed",
        ["economy.chargeServiceCosts"] = "Service costs",
        ["economy.defaultManufacturingIndex"] = "Default manufacturing index",
        ["developmentSupply.testingMarket"] = "Testing market",
        ["developmentSupply.testingGrants"] = "Initial wallet grants",
        ["developmentSupply.institutionalStock"] = "Institutional stock",
        ["developmentSupply.testingStock"] = "Testing stock",
        ["developmentSupply.testingPriceCents"] = "Testing price (ISK)",
        ["developmentSupply.grantCents"] = "Initial grant (ISK)",
        ["resources.renewBelts"] = "Belt renewal",
        ["resources.renewalHourUtc"] = "Renewal hour (UTC)",
        ["npcPopulation.pirates"] = "New pirates",
        ["npcPopulation.guards"] = "New guards",
        ["npcPopulation.convoys"] = "New convoys",
        ["npcPopulation.mining"] = "New mining populations",
        ["npcPopulation.majorThreatAnomalies"] = "Major Threat anomaly tiers",
        ["economy.progressiveMarkets"] = "Progressive regional markets",
        ["capsuleers.enabled"] = "Simulated pilots",
        ["capsuleers.miners"] = "Simulated miners",
        ["capsuleers.trafficMultiplier"] = "Station traffic density",
        ["capsuleers.gateVisitors"] = "Visitors per watched gate",
        ["capsuleers.globalProvisioning"] = "Legacy global provisioning preference",
        ["progression.grantStartingSkills"] = "Starting skills for new pilots",
        ["simulation.checkpointSeconds"] = "Checkpoint interval (seconds)",
        ["simulation.marketPollMilliseconds"] = "Market polling (milliseconds)",
        ["simulation.resourceCheckSeconds"] = "Resource checks (seconds)",
        ["navigation.autopilotGateLandingMeters"] = "Autopilot gate landing distance (metres)",
    };

    private static readonly HashSet<string> s_multipliers =
    [
        "capsuleers.trafficMultiplier",
        "resources.oreYieldMultiplier",
        "resources.iceYieldMultiplier",
        "resources.gasYieldMultiplier",
        "miningHolds.generalMultiplier",
        "miningHolds.iceMultiplier",
        "miningHolds.gasMultiplier",
        "rewards.bountyMultiplier",
        "rewards.missionIskMultiplier",
        "rewards.missionLpMultiplier",
        "rewards.missionStandingMultiplier",
        "progression.killSkillPointMultiplier",
        "progression.miningSkillPointMultiplier",
        "industry.manufacturingSpeed",
        "industry.researchSpeed",
        "industry.copyingSpeed",
        "industry.inventionSpeed",
        "industry.reprocessingEfficiency",
        "planetaryIndustry.extractionYieldMultiplier",
        "planetaryIndustry.factoryOutputMultiplier",
        "planetaryIndustry.cpuMultiplier",
        "planetaryIndustry.powergridMultiplier",
        "structures.anchoringSpeed",
        "structures.onliningSpeed",
        "structures.unanchoringSpeed",
    ];

    internal static IReadOnlyList<SettingsComparisonRow> Settings(WorldSettings original, WorldSettings active, WorldSettings? pending)
    {
        var before = Values(original);
        var current = Values(active);
        var next = pending is null ? null : Values(pending);
        return s_labels
            .Select(label => new SettingsComparisonRow(
                label.Value,
                before[label.Key],
                current[label.Key],
                next?.GetValueOrDefault(label.Key) ?? "—"
            ))
            .ToArray();
    }

    internal static IReadOnlyList<RevisionDifference> Compare(WorldConfigurationDraft before, WorldConfigurationDraft after)
    {
        var left = Values(before.Settings);
        var right = Values(after.Settings);
        List<RevisionDifference> rows = s_labels
            .Where(label => left[label.Key] != right[label.Key])
            .Select(label => new RevisionDifference(label.Value, left[label.Key], right[label.Key]))
            .ToList();
        AddOverrides(
            rows,
            "Adjusted price",
            before.AdjustedPrices.ToDictionary(value => value.TypeId, value => value.PriceIsk),
            after.AdjustedPrices.ToDictionary(value => value.TypeId, value => value.PriceIsk)
        );
        AddOverrides(
            rows,
            "Manufacturing index",
            before.ManufacturingIndices.ToDictionary(value => value.SystemId, value => value.Index),
            after.ManufacturingIndices.ToDictionary(value => value.SystemId, value => value.Index)
        );
        return rows;
    }

    private static void AddOverrides(
        List<RevisionDifference> rows,
        string label,
        IReadOnlyDictionary<int, decimal> left,
        IReadOnlyDictionary<int, decimal> right
    )
    {
        foreach (int key in left.Keys.Union(right.Keys).Order())
        {
            string before = left.TryGetValue(key, out decimal a)
                ? a.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "Original";
            string after = right.TryGetValue(key, out decimal b)
                ? b.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "Original";
            if (before != after)
            {
                rows.Add(new($"{label} · {key}", before, after));
            }
        }
    }

    private static Dictionary<string, string> Values(WorldSettings settings) =>
        JsonSerializer
            .SerializeToElement(settings, JsonSerializerOptions.Web)
            .EnumerateObject()
            .SelectMany(group =>
                group
                    .Value.EnumerateObject()
                    .Select(field => new KeyValuePair<string, string>(
                        group.Name + "." + field.Name,
                        field.Name is "testingPriceCents" or "grantCents"
                            ? (field.Value.GetDecimal() / 100m).ToString(System.Globalization.CultureInfo.InvariantCulture)
                            : field.Value.ValueKind switch
                            {
                                JsonValueKind.True => "On",
                                JsonValueKind.False => "Off",
                                _ when s_multipliers.Contains(group.Name + "." + field.Name) => field.Value.GetDecimal() + "×",
                                _ => field.Value.GetRawText(),
                            }
                    ))
            )
            .ToDictionary();
}
