using Eden.Data.Catalog;
using Eden.Data.Catalog.FactionWarfare;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.World.Catalog;
using Eden.World.FactionWarfare;

namespace Eden.World.Loot;

internal sealed partial class WreckStore
{
    private sealed record MilitiaWreckLoot(CatalogItemDefinition Wreck, NpcLootDrop Drop);

    private static MilitiaWreckLoot? MilitiaLoot(CatalogDbContext catalog, WorldNpc npc)
    {
        if (
            npc is not { Role: NpcRole.MilitiaComplexDefender, AnchorKind: NpcAnchorKind.MilitiaComplex }
            || MilitiaDefenderLoot.For((MilitiaFaction)npc.FactionId) is not { } loot
        )
        {
            return null;
        }
        var faction = (MilitiaFaction)npc.FactionId;
        if (
            npc.TypeId != MilitiaComplexCatalog.DefenderType(faction, MilitiaComplexFamily.Scout)
            && npc.TypeId != MilitiaComplexCatalog.DefenderType(faction, MilitiaComplexFamily.Small)
        )
        {
            return null;
        }
        int tag = loot.Tag(NpcSpawnRules.Seed(npc.SystemId, npc.Id));
        return
            catalog.FittingType(loot.WreckTypeId) is { GroupId: WreckDefinitions.Group } wreck
            && catalog.FittingType(tag) is { CategoryId: (int)InventoryCategory.Commodity }
            ? new(wreck, new(tag, 1))
            : null;
    }
}
