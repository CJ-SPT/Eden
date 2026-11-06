using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Eden.Gameplay.Inventory;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Fixtures;

internal static class PassiveCatalogFixture
{
    private const int OriginalIbisType = 601;

    private sealed record AttributeValue(int Id, double Value);

    private sealed record TypeDefinition(
        int Id,
        string Name,
        int GroupId,
        string GroupName,
        int CategoryId,
        double? Mass,
        double? Radius,
        double? Capacity,
        IReadOnlyList<AttributeValue> Attributes
    );

    private sealed record Record(string Dataset, int Key, JsonElement Data);

    private sealed record Fixture(int Build, IReadOnlyList<TypeDefinition> Types, IReadOnlyList<Record> Records);

    private static readonly ConcurrentDictionary<string, Lazy<Fixture>> Fixtures = new(StringComparer.Ordinal);

    private static Fixture ReadFixture(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Eden.Protocol.Tests.Fixtures.{resource}")!;
        var data = JsonSerializer.Deserialize<Fixture>(stream, JsonSerializerOptions.Web)!;
        Assert.Equal(3662168, data.Build);
        return data;
    }

    // Minimal definitions from the read-only build 3662168 catalog. No world
    // inventory, account, market or resource-cache data is embedded.
    internal static void Load(string path) => Load(path, "PassiveCatalog.json", null);

    internal static void LoadFrigateHulls(string path) => Load(path, "FrigateHullCatalog.json", null);

    internal static void LoadSmallHulls(string path, IReadOnlySet<int>? types = null) => Load(path, "SmallHullCatalog.json", types);

    internal static void LoadSmallHull(string path, int type) => Load(path, "SmallHullCatalog.json", null, hullType: type);

    internal static void LoadHoldCargo(string path) =>
        Load(
            path,
            "UtilityHullCatalog.json",
            new HashSet<int>
            {
                34,
                1230,
                25268,
                16262,
                16272,
                3645,
                3683,
                3699,
                28668,
                33474,
                2488,
                23055,
                2524,
                25,
                587,
                33513,
                3898,
                16273,
                16275,
                45586,
            }
        );

    internal static void LoadUtilityHulls(string path, IReadOnlySet<int>? types = null) => Load(path, "UtilityHullCatalog.json", types);

    internal static void LoadUtilityHull(string path, int type) => Load(path, "UtilityHullCatalog.json", null, hullType: type);

    internal static void LoadCombatHulls(string path, IReadOnlySet<int>? types = null) => Load(path, "CombatHullCatalog.json", types);

    internal static void LoadCombatDrones(string path) => Load(path, "CombatDroneCatalog.json", null);

    internal static void LoadCombatHull(string path, int type) => Load(path, "CombatHullCatalog.json", null, hullType: type);

    internal static void LoadTiers(string path, IReadOnlySet<int>? types = null) => Load(path, "PassiveTierCatalog.json", types);

    internal static void LoadActiveTiers(string path, IReadOnlySet<int>? types = null) => Load(path, "ActiveTierCatalog.json", types);

    internal static void LoadActiveMetadata(string path, IReadOnlySet<int> types) => Load(path, "ActiveTierCatalog.json", types, false);

    internal static void LoadWeaponTiers(string path, IReadOnlySet<int>? types = null) => Load(path, "HybridTierCatalog.json", types);

    internal static void LoadWeaponMetadata(string path, IReadOnlySet<int> types) => Load(path, "HybridTierCatalog.json", types, false);

    internal static void LoadProjectileTiers(string path, IReadOnlySet<int>? types = null) =>
        Load(path, "ProjectileTierCatalog.json", types);

    internal static void LoadHullRepairTiers(string path, IReadOnlySet<int>? types = null) =>
        Load(path, "HullRepairTierCatalog.json", types);

    internal static void LoadEnergyTiers(string path, IReadOnlySet<int>? types = null) => Load(path, "EnergyTierCatalog.json", types);

    internal static void LoadMissileTiers(string path, IReadOnlySet<int>? types = null) => Load(path, "MissileTierCatalog.json", types);

    internal static void LoadSupportTiers(string path, IReadOnlySet<int>? types = null) => Load(path, "SupportTierCatalog.json", types);

    internal static void LoadTackleTiers(string path, IReadOnlySet<int>? types = null) => Load(path, "TackleTierCatalog.json", types);

    internal static void LoadNpcPopulation(string path)
    {
        Load(path, "NpcPopulationCatalog.json", null);
        using CatalogDbContext catalog = new(path, writable: true);
        foreach (var source in catalog.Set<SourceRecord>().Where(r => r.Dataset == "sde/npcCorporations").ToArray())
        {
            int id = int.Parse(source.Key, CultureInfo.InvariantCulture);
            string name = source.Read<SdeNpcCorporations>().Name?.En ?? $"NPC {id}";
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO NpcCorporation VALUES ({id},{name})");
        }
    }

    internal static void LoadNpcConvoys(string path) => Load(path, "NpcConvoyCatalog.json", null);

    internal static void LoadNpcMining(string path)
    {
        Load(path, "NpcMiningCatalog.json", null);
        using CatalogDbContext catalog = new(path, writable: true);
        foreach (var source in catalog.Set<SourceRecord>().Where(r => r.Dataset == "sde/npcCorporations").ToArray())
        {
            int id = int.Parse(source.Key, CultureInfo.InvariantCulture);
            string name = source.Read<SdeNpcCorporations>().Name?.En ?? $"NPC {id}";
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO NpcCorporation VALUES ({id},{name})");
        }
    }

    private static void Load(string path, string resource, IReadOnlySet<int>? types, bool definitions = true, int? hullType = null)
    {
        var data = Fixtures.GetOrAdd(resource, name => new Lazy<Fixture>(() => ReadFixture(name))).Value;
        if (hullType is int hull)
        {
            // Each lifecycle/bonus case needs its hull, the original Ibis and
            // all fixture equipment/skills. Cohort audits still load every hull.
            types = data
                .Types.Where(t => t.CategoryId != (int)InventoryCategory.Ship || t.Id == hull || t.Id == OriginalIbisType)
                .Select(t => t.Id)
                .ToHashSet();
        }
        using CatalogDbContext catalog = new(path, writable: true);
        using var transaction = catalog.Database.BeginTransaction();
        foreach (var type in data.Types.Where(type => definitions && (types is null || types.Contains(type.Id))))
        {
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO ItemCategory VALUES ({type.CategoryId},{type.CategoryId.ToString()})");
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO ItemGroup VALUES ({type.GroupId},{type.GroupName},{type.CategoryId})");
            catalog.Database.ExecuteSql(
                $"""
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Mass,Radius,Capacity)
                VALUES ({type.Id},{type.Name},{type.GroupId},{type.Mass},{type.Radius},{type.Capacity})
                """
            );
            foreach (var attribute in type.Attributes)
            {
                catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO DogmaAttribute VALUES ({attribute.Id},{attribute.Id.ToString()})");
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES ({type.Id},{attribute.Id},{attribute.Value})");
            }
        }
        foreach (var record in data.Records)
        {
            if (types is not null && record.Dataset is "client/types" or "client/typedogma" && !types.Contains(record.Key))
            {
                continue;
            }
            string key = record.Key.ToString(CultureInfo.InvariantCulture);
            string json = record.Data.GetRawText();
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ({record.Dataset},{key},{json})");
        }
        transaction.Commit();
    }
}
