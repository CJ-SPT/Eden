using System.Collections.Frozen;
using System.Text.Json;
using Eden.Data.Catalog.Exploration;
using Eden.Gameplay.Combat;

namespace Eden.World.Exploration;

internal static class SleeperLootCatalog
{
    private static readonly Lazy<FrozenDictionary<int, IReadOnlyList<NpcLootDrop>>> s_drops = new(Load);

    internal static IReadOnlyList<NpcLootDrop>? Drops(int typeId) => s_drops.Value.GetValueOrDefault(typeId);

    private static FrozenDictionary<int, IReadOnlyList<NpcLootDrop>> Load()
    {
        using var stream = typeof(SleeperLootDefinitions).Assembly.GetManifestResourceStream("Eden.Data.SleeperLoot.json")!;
        var file = JsonSerializer.Deserialize<SleeperLootDefinitions>(stream, JsonSerializerOptions.Web)!;
        return file.Npcs.ToFrozenDictionary(n => n.TypeId, n => n.Drops);
    }
}
