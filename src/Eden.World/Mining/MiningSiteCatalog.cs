using System.Collections.Frozen;
using System.Text.Json;
using Eden.Data.Catalog.Mining;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;

namespace Eden.World.Mining;

internal static class MiningSiteCatalog
{
    private static readonly Lazy<IReadOnlyList<MiningSiteDefinition>> s_sites = new(Load);
    internal static IReadOnlyList<MiningSiteDefinition> Sites => s_sites.Value;
    private static readonly Lazy<FrozenSet<int>> s_gasTypes = new(() =>
        Sites.Where(s => s.Family == MiningFamily.Gas).SelectMany(s => s.Deposits).Select(d => d.TypeId).ToFrozenSet()
    );

    internal static bool SupportsGas(int typeId) => s_gasTypes.Value.Contains(typeId);

    private static IReadOnlyList<MiningSiteDefinition> Load()
    {
        using var stream = typeof(MiningSiteCatalog).Assembly.GetManifestResourceStream("Eden.World.MiningSites.json")!;
        var file =
            JsonSerializer.Deserialize<MiningSiteDefinitions>(stream, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Missing mining sites.");
        if (
            file.Build != CatalogSources.PinnedBuild
            || file.Sites.Select(s => s.DungeonId).Distinct().Count() != file.Sites.Count
            || file.Sites.Any(s =>
                s.Family is not (MiningFamily.Gas or MiningFamily.Ice)
                || (s.Family == MiningFamily.Ice && s.Systems is not { Count: > 0 })
                || (
                    s.WormholeClasses is { } classes
                    && (s.Family != MiningFamily.Gas || classes.Count == 0 || classes.Any(c => c is < 1 or > 6))
                )
                || s.Deposits.Count is < 1 or >= MiningSiteRules.DepositsPerSite
                || s.Deposits.Any(d => d.Quantity <= 0 || !float.IsFinite(d.Radius) || d.Radius < 0)
            )
        )
        {
            throw new InvalidDataException("Invalid pinned mining-site definitions.");
        }
        return file.Sites;
    }
}
