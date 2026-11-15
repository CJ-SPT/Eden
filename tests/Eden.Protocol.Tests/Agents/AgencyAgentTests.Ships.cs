using System.Text.Json.Nodes;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class AgencyAgentTests
{
    private const int GatedMission = 875;
    private const int GatedDungeon = 213;
    private const int Ibis = 601;
    private const int Capsule = 670;

    [Fact]
    public void ShipPreviewCombinesTypeListsRaceExclusionsAndDefaultGateRules()
    {
        MissionShipType[] ships = [new(4, 29, 0), new(3, 25, 0), new(2, 25, 2), new(1, 25, 1)];
        var onlyFrigates = new MissionGateAccess(
            new InventoryTypeList(
                new HashSet<long>(),
                new HashSet<long> { 25 },
                new HashSet<long>(),
                new HashSet<long>(),
                new HashSet<long>(),
                new HashSet<long> { 3 }
            ),
            new HashSet<long> { 1 },
            null,
            null
        );
        var restricted = MissionShipRestrictionRules.Describe(ships, [onlyFrigates, MissionGateAccess.Default]);
        Assert.Equal([1], restricted.AllowedShipTypes);
        Assert.Equal([2, 3, 4], restricted.RestrictedShipTypes);
        Assert.True(restricted.NonDefaultShipRestrictions);
        var raceOnly = MissionShipRestrictionRules.Describe(ships, [onlyFrigates with { AllowedShips = null }]);
        Assert.Equal([1, 3, 4], raceOnly.AllowedShipTypes);
        var defaults = MissionShipRestrictionRules.Describe(ships, [MissionGateAccess.Default]);
        Assert.Equal([1, 2, 3], defaults.AllowedShipTypes);
        Assert.Equal([4], defaults.RestrictedShipTypes);
        Assert.False(defaults.NonDefaultShipRestrictions);
        var ungated = MissionShipRestrictionRules.Describe(ships, []);
        Assert.Equal([1, 2, 3, 4], ungated.AllowedShipTypes);
        Assert.Empty(ungated.RestrictedShipTypes);
    }

    [Fact]
    public void ShipPreviewIntersectsEveryGateAndPreservesEarlierSnapshots()
    {
        MissionShipType[] ships = [new(1, 25, 1), new(2, 25, 2), new(3, 25, 0)];
        var caldari = new MissionGateAccess(null, new HashSet<long> { 1 }, null, null);
        var minmatar = caldari with { AllowedRaces = new HashSet<long> { 2 } };
        var first = MissionShipRestrictionRules.Describe(ships, [caldari]);
        var both = MissionShipRestrictionRules.Describe(ships, [caldari, minmatar]);
        Assert.Equal([1, 3], first.AllowedShipTypes);
        Assert.Equal([3], both.AllowedShipTypes);
        Assert.Equal([1, 2], both.RestrictedShipTypes);
        Assert.Equal([1, 3], first.AllowedShipTypes);
    }

    [Fact]
    public async Task RestrictedCardMatchesCatalogGateAccessAndRefreshesAfterCatalogChanges()
    {
        using MissionFixture f = new(
            division: MissionRules.SecurityDivision,
            live: true,
            configure: catalog =>
            {
                catalog.Database.ExecuteSqlRaw("DELETE FROM SourceRecord WHERE Dataset='client/missions' AND Key!='875'");
                var dungeon = JsonNode.Parse(
                    catalog.Set<Eden.Data.Catalog.SourceRecord>().Single(r => r.Dataset == "client/dungeons" && r.Key == "213").Data
                )!;
                dungeon["connections"]![0]!["allowedShipsList"] = 459;
                dungeon["connections"]![0]!["allowedShipRaces"] = new JsonArray(1);
                string updated = dungeon.ToJsonString();
                catalog.Database.ExecuteSql($"UPDATE SourceRecord SET Data={updated} WHERE Dataset='client/dungeons' AND Key='213'");
            }
        );
        var offer = f.Accept(f.Request());
        Assert.Equal(GatedMission, offer.MissionId);
        Assert.True(offer.HasShipRestrictions);
        var gate = Assert.Single(f.Missions.Sites(offer.SystemId).SelectMany(s => s.Gates));
        var all = f.Missions.ShipRestrictions(MissionFixture.PilotId, MissionFixture.AgentId, GatedDungeon);
        Assert.Contains(Ibis, all.AllowedShipTypes);
        Assert.Contains(Capsule, all.RestrictedShipTypes);
        Assert.True(all.NonDefaultShipRestrictions);
        var byGate = f.Missions.ShipRestrictions(MissionFixture.PilotId, MissionFixture.AgentId, GatedDungeon, gate.DungeonObjectId);
        Assert.Equal(all.AllowedShipTypes, byGate.AllowedShipTypes);
        Assert.Equal(all.RestrictedShipTypes, byGate.RestrictedShipTypes);
        var info = Fields(MissionFixture.Reply(f.Call(AgentService, "GetMissionJournalInfo", [null, null])));
        var dungeonInfo = Fields(Assert.Single(Assert.IsType<List<object?>>(Fields(info["objectives"])["dungeons"])));
        Assert.Equal(1L, dungeonInfo["shipRestrictions"]);
        Assert.Equal((long)offer.EnemyFactionId, dungeonInfo["ownerID"]);
        var wire = Fields(
            MissionFixture.Reply(f.Call(AgentService, "GetDungeonShipRestrictions", [(long)GatedDungeon, gate.DungeonObjectId]))
        );
        Assert.Contains((long)Ibis, Assert.IsType<List<object?>>(wire["allowedShipTypes"]));
        Assert.Contains((long)Capsule, Assert.IsType<List<object?>>(wire["restrictedShipTypes"]));

        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            var list = JsonNode.Parse(
                catalog.Set<Eden.Data.Catalog.SourceRecord>().Single(r => r.Dataset == "client/typelist" && r.Key == "459").Data
            )!;
            list["excludedTypeIDs"] = new JsonArray(Ibis);
            string updated = list.ToJsonString();
            catalog.Database.ExecuteSql($"UPDATE SourceRecord SET Data={updated} WHERE Dataset='client/typelist' AND Key='459'");
        }
        var refreshed = f.Missions.ShipRestrictions(MissionFixture.PilotId, MissionFixture.AgentId, GatedDungeon);
        Assert.Contains(Ibis, refreshed.RestrictedShipTypes);
        Assert.DoesNotContain(Ibis, refreshed.AllowedShipTypes);
        Assert.Contains(Ibis, all.AllowedShipTypes);
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        Assert.Equal(offer.Id, f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.Id);
    }
}
