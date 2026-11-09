using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class UniversalSearchTests
{
    [Fact]
    public void MixedNativeSearchFindsEveryAvailableCategoryAndResolvesOwners()
    {
        using TestWorld db = new();
        Prepare(db);
        PlayerCorporationTests.Prepare(db);
        int corporation = db
            .Services.GetRequiredService<IPlayerCorporations>()
            .Create(90000001, PlayerCorporationTests.Details())
            .Corporation.Id;
        using CourierServiceTests.Client client = new(db, CalendarTestData.Clock(), 1000001);
        Assert.Same(PilotCallResult.Unsupported, client.Call("Query", Args("Jita", SearchCategory.SolarSystem), "search"));
        client.Refresh();
        var examples = new (SearchCategory Category, string Query, long Id)[]
        {
            (SearchCategory.Agent, "Agent Étoile", AgentTestData.AgentId),
            (SearchCategory.Character, "Public CEO", AgentTestData.NonAgent),
            (SearchCategory.Character, "Eden Pilot", 90000001),
            (SearchCategory.Corporation, "Caldari Navy", 1000035),
            (SearchCategory.Corporation, "Eden Explorers", corporation),
            (SearchCategory.Faction, "Caldari State", 500001),
            (SearchCategory.Constellation, "Kimotoro", 20000020),
            (SearchCategory.SolarSystem, "Jita", 30000142),
            (SearchCategory.Region, "The Forge", 10000002),
            (SearchCategory.Station, "Jita", 60003760),
            (SearchCategory.ItemType, "Ibis", 601),
        };
        foreach (var (category, query, id) in examples)
        {
            var grouped = Assert.IsType<Dictionary<long, object?>>(Reply(client.Call("Query", Args(query, category), "search")));
            Assert.Contains(id, Ids(grouped[(long)category]));
            Assert.Contains(id, Ids(Reply(client.Call("QuickQuery", Args(query, category), "search"))));
        }
        var mixed = Ids(Reply(client.Call("QuickQuery", Args("Agent", SearchCategory.Agent, SearchCategory.Character), "search")));
        Assert.Equal(new long[] { AgentTestData.AgentId, AgentTestData.OtherAgent }, mixed);
        Assert.DoesNotContain(AgentTestData.DeletedAgent, mixed);
        var owners = BlueRead.Tuple(
            Reply(client.Call("GetMultiOwnersEx", [new List<object?> { 1000035L, 500001L, (long)corporation }], "config")),
            2
        );
        var ownerRows = Assert.IsType<List<object?>>(owners[1]).Select(row => BlueRead.Tuple(row, 5)).ToArray();
        Assert.Contains(ownerRows, row => Equals(row[0], 1000035L) && BlueMarshal.Text(row[1]) == "Caldari Navy" && Equals(row[2], 2L));
        Assert.Contains(ownerRows, row => Equals(row[0], 500001L) && BlueMarshal.Text(row[1]) == "Caldari State" && Equals(row[2], 30L));
        Assert.Contains(ownerRows, row => Equals(row[0], (long)corporation) && BlueMarshal.Text(row[1]) == "Eden Explorers");
        Assert.Empty(client.Context.Notifications);
    }

    [Fact]
    public void NpcOptionsEmptyCategoriesAndAlternateNamesPreserveNativeShapes()
    {
        using TestWorld db = new();
        Prepare(db);
        using CourierServiceTests.Client client = new(db, CalendarTestData.Clock(), 1000001);
        client.Refresh();
        Assert.Empty(
            Ids(
                Reply(
                    client.Call(
                        "QuickQuery",
                        Args("Agent", SearchCategory.Agent, SearchCategory.Character),
                        "search",
                        new() { ["hideNPC"] = true }
                    )
                )
            )
        );
        Assert.Equal(
            new long[] { 90000001 },
            Ids(Reply(client.Call("QuickQuery", Args("Eden Pilot", SearchCategory.Character), "search", new() { ["hideNPC"] = 1L })))
        );
        Assert.Empty(
            Ids(Reply(client.Call("QuickQuery", Args("Caldari", SearchCategory.Corporation), "search", new() { ["hideNPC"] = true })))
        );
        Assert.Empty(
            Ids(Reply(client.Call("QuickQuery", Args("Jita", SearchCategory.SolarSystem), "search", new() { ["onlyAltName"] = true })))
        );
        foreach (
            var category in new[]
            {
                SearchCategory.Alliance,
                SearchCategory.Wormhole,
                SearchCategory.Structure,
                SearchCategory.StructureWithInlinedData,
            }
        )
        {
            Assert.Empty(Ids(Reply(client.Call("QuickQuery", Args("Anything", category), "search"))));
            Assert.Empty(Assert.IsType<Dictionary<string, object?>>(Reply(client.Call("Query", Args("Anything", category), "search"))));
        }
    }

    [Fact]
    public void SavedNamesAreFreshAcrossCreationRenameAndReopen()
    {
        using TestWorld db = new();
        Prepare(db);
        var search = db.Services.GetRequiredService<IUniversalSearch>();
        SearchRequest request = new("New", [SearchCategory.Character, SearchCategory.Corporation]);
        Assert.All(search.Search(request), group => Assert.Empty(group.Ids));
        var creation = db.Services.GetRequiredService<ICharacterCreation>();
        TestCatalog.AddSchool(new WorldDatabase(db.Path).CatalogPath, 19, 1, 1, 30001407, 60015161, 601);
        long pilot = creation.Create(1000001, new("New Search Pilot", 1, 1, 0, 64, [1, 2], [3, 4], 19));
        PlayerCorporationTests.Prepare(db);
        int corporation = db
            .Services.GetRequiredService<IPlayerCorporations>()
            .Create(90000001, PlayerCorporationTests.Details("New Search Corp"))
            .Corporation.Id;
        Assert.Equal(new long[] { pilot }, search.Search(request)[0].Ids);
        Assert.Equal(new long[] { corporation }, search.Search(request)[1].Ids);
        // Names are authoritative saved inputs; no rename UI is added by this milestone.
        using (WorldDbContext saved = new(db.Path))
        {
            saved.Database.ExecuteSql($"UPDATE WorldPilot SET Name='Renamed Search Pilot' WHERE Id={pilot}");
            saved.Database.ExecuteSql(
                $"""
                UPDATE WorldCorporation
                SET Name='Renamed Search Corp', DetailsJson=json_set(DetailsJson,'$.Name','Renamed Search Corp')
                WHERE Id={corporation}
                """
            );
        }
        Assert.All(search.Search(request), group => Assert.Empty(group.Ids));
        var renamed = request with { Query = "Renamed" };
        Assert.Equal(new long[] { pilot }, search.Search(renamed)[0].Ids);
        Assert.Equal(new long[] { corporation }, search.Search(renamed)[1].Ids);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new WorldDatabase(db.Path), liveWorld: false);
        var after = reopened.GetRequiredService<IUniversalSearch>().Search(renamed);
        Assert.Equal(new long[] { pilot }, after[0].Ids);
        Assert.Equal(new long[] { corporation }, after[1].Ids);
    }

    [Fact]
    public void PublishedCatalogResultsAreBoundedSortedAndInvalidatedAfterCatalogChange()
    {
        using TestWorld db = new();
        Prepare(db);
        var search = db.Services.GetRequiredService<IUniversalSearch>();
        SearchRequest request = new("Search item", [SearchCategory.ItemType]);
        Assert.Empty(search.Search(request)[0].Ids);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            for (int index = 0; index < 505; index++)
            {
                int id = 100000 + index;
                string name = $"Search item {index:D3}";
                catalog.Database.ExecuteSql($"INSERT INTO ItemType(Id,Name,GroupId) SELECT {id},{name},GroupId FROM ItemType WHERE Id=601");
                string key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string data = JsonSerializer.Serialize(new { published = index == 0 ? 0 : 1 });
                catalog.Database.ExecuteSql($"INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('client/types',{key},{data})");
            }
        }
        var results = search.Search(request)[0].Ids;
        Assert.Equal(CharacterSearchRules.MaximumResults, results.Count);
        Assert.Equal(100001, results[0]);
        Assert.Equal(100500, results[^1]);
        Assert.Equal(results, search.Search(request)[0].Ids);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(14L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void InvalidCategoryBoundariesFailWithoutChangingState(long category)
    {
        using TestWorld db = new();
        using CourierServiceTests.Client client = new(db, CalendarTestData.Clock(), 1000001);
        client.Refresh();
        var before = db.Store.FindPilot(1000001);
        Assert.IsType<PilotCallResult.Failure>(client.Call("Query", ["Jita", new List<object?> { category }], "search"));
        Assert.Equal(before, db.Store.FindPilot(1000001));
        Assert.Empty(client.Context.Notifications);
    }

    internal static void Prepare(TestWorld db)
    {
        AgentTestData.Prepare(db);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR REPLACE INTO SourceRecord(Dataset,Key,Data) VALUES
            ('client/factions','500001','{{"nameID":900001}}'),
            ('client/localization_fsd_en-us','900001','{{"text":"Caldari State","language":"en-us"}}');
            UPDATE SourceRecord SET Data=json_set(Data,'$.published',1) WHERE Dataset='client/types' AND Key='601';
            """
        );
    }

    private static object?[] Args(string query, params SearchCategory[] categories) =>
        [new BlueUnicode(query), categories.Select(category => (object?)(long)category).ToList()];

    private static long[] Ids(object? value) => Assert.IsType<List<object?>>(value).Cast<long>().ToArray();

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));
}
