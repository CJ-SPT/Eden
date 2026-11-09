using System.Text.Json;
using Eden.Gameplay.Configuration;

namespace Eden.Server.Administration;

internal sealed record SettingsComparisonRow(string Label, string Original, string Active, string Pending);

internal sealed record RevisionDifference(string Field, string Before, string After);

internal static class ConfigurationDisplay
{
    private static readonly IReadOnlyDictionary<string, string> s_labels = new Dictionary<string, string>
    {
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
        ["capsuleers.globalProvisioning"] = "Legacy global provisioning preference",
        ["progression.grantStartingSkills"] = "Starting skills for new pilots",
        ["simulation.checkpointSeconds"] = "Checkpoint interval (seconds)",
        ["simulation.marketPollMilliseconds"] = "Market polling (milliseconds)",
        ["simulation.resourceCheckSeconds"] = "Resource checks (seconds)",
    };

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
                                _ => field.Value.GetRawText(),
                            }
                    ))
            )
            .ToDictionary();
}
