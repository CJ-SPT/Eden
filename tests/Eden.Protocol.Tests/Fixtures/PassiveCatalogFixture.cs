using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Fixtures;

internal static class PassiveCatalogFixture
{
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

    // Minimal definitions from the read-only build 3662168 catalog. No world
    // inventory, account, market or resource-cache data is embedded.
    internal static void Load(string path) => Load(path, "PassiveCatalog.json", null);

    internal static void LoadTiers(string path, IReadOnlySet<int>? types = null) => Load(path, "PassiveTierCatalog.json", types);

    internal static void LoadActiveTiers(string path, IReadOnlySet<int>? types = null) => Load(path, "ActiveTierCatalog.json", types);

    internal static void LoadActiveMetadata(string path, IReadOnlySet<int> types) => Load(path, "ActiveTierCatalog.json", types, false);

    private static void Load(string path, string resource, IReadOnlySet<int>? types, bool definitions = true)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Eden.Protocol.Tests.Fixtures.{resource}")!;
        var data = JsonSerializer.Deserialize<Fixture>(stream, JsonSerializerOptions.Web)!;
        Assert.Equal(3662168, data.Build);
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
