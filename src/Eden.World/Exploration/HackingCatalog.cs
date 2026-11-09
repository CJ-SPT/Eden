using System.Collections.Immutable;
using System.Text.Json;
using Eden.Data.Catalog.Exploration;
using Eden.Gameplay.Content;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Exploration;

internal static class HackingCatalog
{
    private const int AbyssalMetaGroup = 15;
    internal const int DatacoreGroup = 333;
    internal const int DecryptorGroup = 1304;
    internal const int SalvageGroup = 754;
    internal static readonly IReadOnlyList<ExplorationDefinition> Sites = Load();
    private static readonly CatalogDefinition<ImmutableArray<int>> s_loot = new("exploration-rewards", LoadLoot);

    internal static ImmutableArray<int> Loot(CatalogDbContext catalog, int group) => catalog.Definition(s_loot, group);

    private static ImmutableArray<int> LoadLoot(CatalogDbContext catalog, int group) =>
        catalog
            .Records(CatalogData.Client.Types)
            .Where(t => t.Value.GroupID == group && t.Value.Published == 1)
            .Select(t => checked((int)t.Key))
            .Order()
            .ToImmutableArray();

    private static readonly CatalogTypeRule s_support = new(typeof(HackingCatalog).FullName!, Validate);

    private static IReadOnlyList<ExplorationDefinition> Load()
    {
        using var source = typeof(ExplorationDefinitions).Assembly.GetManifestResourceStream("Eden.Data.PirateExploration.json")!;
        var data = JsonSerializer.Deserialize<ExplorationDefinitions>(source, JsonSerializerOptions.Web)!;
        if (
            data.Build != BuildContentPolicy.PinnedBuild
            || data.Sites.Any(s => s.Containers.Count is < 1 or >= HackingRules.ContainersPerSite)
        )
        {
            throw new InvalidDataException("Invalid pinned exploration definitions.");
        }
        return data.Sites;
    }

    internal static bool Supported(CatalogDbContext catalog, int type, int group) => s_support.Supports(catalog, type, group);

    private static bool Validate(CatalogDbContext catalog, int typeId, int group)
    {
        if (group != HackingRules.AnalyzerGroup)
        {
            return false;
        }
        var type = catalog.Read(CatalogData.Client.Types, (long)typeId);
        var a = catalog.FittingAttributes(typeId);
        var effects = PassiveCatalog.Effects(catalog, typeId);
        return type?.Published == 1
            && type.MetaGroupID != AbyssalMetaGroup
            && FittingCatalog.DefaultEffect(catalog, typeId) == DogmaEffect.Hacking
            && a.GetValueOrDefault(DogmaAttributeId.VirusCoherence) > 0
            && a.GetValueOrDefault(DogmaAttributeId.VirusStrength) > 0
            && a.GetValueOrDefault(DogmaAttributeId.VirusSlots) is >= 1 and <= HackingRules.MaximumSlots
            && effects.Count == FittingCatalog.Effects(catalog, typeId).Count
            && effects.Any(e => e.EffectID == (int)DogmaEffect.Online)
            && effects.Any(e => e.EffectID == (int)DogmaEffect.MediumPower)
            && effects.All(e =>
                e.EffectID == (int)DogmaEffect.Hacking
                    ? e.EffectCategory == (int)PassiveEffectPhase.Targeted
                        && e.RangeAttributeID == (int)DogmaAttributeId.MaxRange
                        && e.DischargeAttributeID == (int)DogmaAttributeId.CapacitorNeed
                        && e.DurationAttributeID == (int)DogmaAttributeId.Duration
                        && (e.ModifierInfo ?? []).Length == 0
                    : e.EffectID == (int)DogmaEffect.Online
                        || e.EffectCategory is 0 or 4
                            && (e.ModifierInfo ?? []).All(m => PassiveCatalog.Parse(m, e.EffectCategory!.Value) is not null)
            );
    }

    internal static AnalyzerModule Module(long id, int type, IReadOnlyDictionary<DogmaAttributeId, double> a) =>
        new(
            id,
            type,
            a.GetValueOrDefault(DogmaAttributeId.Duration),
            a.GetValueOrDefault(DogmaAttributeId.CapacitorNeed),
            a.GetValueOrDefault(DogmaAttributeId.MaxRange),
            checked((int)a.GetValueOrDefault(DogmaAttributeId.VirusCoherence)),
            checked((int)a.GetValueOrDefault(DogmaAttributeId.VirusStrength)),
            checked((int)a.GetValueOrDefault(DogmaAttributeId.VirusSlots))
        );
}
