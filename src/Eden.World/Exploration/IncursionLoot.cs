using System.Collections.Immutable;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Loot;
using Eden.World.Runtime.Economy;

namespace Eden.World.Exploration;

internal static class IncursionLoot
{
    // Published build-3662168 True Sansha modules and Sansha hull/fighter blueprints.
    private static readonly int[] s_moduleTypes =
    [
        13832,
        13943,
        13956,
        13970,
        13974,
        13978,
        13982,
        14140,
        14170,
        14174,
        14248,
        14258,
        14268,
        22568,
        28383,
    ];
    private static readonly int[] s_blueprintTypes = [2949, 17719, 17737, 17925];
    private const int RevenantBlueprint = 3515;
    private const int RevenantRolls = 20;
    private const int MaximumModules = 3;

    private sealed record LootTable(ImmutableArray<int> Modules, ImmutableArray<int> Blueprints, bool Revenant);

    private static readonly CatalogDefinition<LootTable> s_table = new(
        "incursion-flagship-loot",
        (catalog, _) =>
            new(
                [.. s_moduleTypes.Where(type => Published(catalog, type, InventoryCategory.Module))],
                [.. s_blueprintTypes.Where(type => Published(catalog, type, InventoryCategory.Blueprint))],
                Published(catalog, RevenantBlueprint, InventoryCategory.Blueprint)
            )
    );

    internal static void Prepare(CatalogDbContext catalog) => _ = catalog.GlobalDefinition(s_table);

    internal static void Create(
        WorldAction world,
        CatalogDbContext catalog,
        WorldCombatAnomaly site,
        Pilot owner,
        IncursionSecurity security,
        long now
    )
    {
        var flagship = world
            .Filter<WorldNpc>(npc => npc.EncounterId == site.Id && npc.DestroyedAt != null)
            .AsEnumerable()
            .SingleOrDefault(npc => IncursionSiteCatalog.IsMothershipFlagship(site.DungeonId, npc.TypeId));
        if (flagship is null)
        {
            return;
        }

        var drops = Roll(catalog, site.SystemId, site.Id, security);
        if (drops.Count != 0)
        {
            WreckStore.Create(world, catalog, flagship, owner, now, drops);
        }
    }

    internal static IReadOnlyList<EncounterWreckDrop> Roll(CatalogDbContext catalog, int systemId, long siteId, IncursionSecurity security)
    {
        var profile = catalog.GlobalDefinition(s_table);
        var random = new Random(unchecked((int)NpcSpawnRules.Seed(systemId, siteId)));
        int[] modules = profile.Modules.ToArray();
        random.Shuffle(modules);
        var drops = modules.Take(random.Next(1, MaximumModules + 1)).Select(type => new EncounterWreckDrop(type)).ToList();
        if (security == IncursionSecurity.Low && profile.Revenant && random.Next(RevenantRolls) == 0)
        {
            drops.Add(new(RevenantBlueprint, BlueprintCopy: true));
        }
        else if (!profile.Blueprints.IsEmpty)
        {
            drops.Add(new(profile.Blueprints[random.Next(profile.Blueprints.Length)], BlueprintCopy: true));
        }
        return drops;
    }

    private static bool Published(CatalogDbContext catalog, int typeId, InventoryCategory category) =>
        catalog.FittingType(typeId)?.CategoryId == (int)category && catalog.Read(CatalogData.Client.Types, (long)typeId)?.Published == 1;
}
