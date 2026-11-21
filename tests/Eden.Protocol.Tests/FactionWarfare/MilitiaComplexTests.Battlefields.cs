using Eden.Data.World.FactionWarfare;
using Eden.Data.World.Market;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.FactionWarfare;

public sealed partial class MilitiaComplexTests
{
    private const int BattlefieldDungeon = 9899;

    [Fact]
    public async Task BattlefieldThreePointScoreAndReplacementSurviveCheckpoint()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(fixture.Path);
        LoadComplexCatalog(database);
        LoadBattlefield(database);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var complexes = fixture.Services.GetRequiredService<IMilitiaComplexes>();
        var defender = fixture.Store.FindPilot(FirstAccount)!;
        var site = Assert.Single(
            complexes.Directory(clock.Now, battlefields: true).SelectMany(zone => zone.Sites),
            candidate => candidate.SystemId == SystemId
        );
        Assert.DoesNotContain(complexes.Directory(clock.Now).SelectMany(zone => zone.Sites), candidate => candidate.Id == site.Id);
        var bodies = complexes.Sites(SystemId, clock.Now).Single(candidate => candidate.Id == site.Id).Objects;
        var zones = bodies.Where(body => body.BattlefieldCapture is not null).ToArray();
        Assert.Equal(3, zones.Length);
        Assert.Equal(zones.Length, zones.Select(body => body.Id).Distinct().Count());
        Assert.Equal(0, Assert.Single(bodies, body => body.BattlefieldScore.HasValue).BattlefieldScore);
        long slot = MilitiaComplexRules.Slot(SystemId, MilitiaComplexFamily.Battlefield);
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(new SavedMilitiaEnlistment { PilotId = defender.PilotId, DirectFaction = MilitiaFaction.Minmatar });
            action.Add(
                new SavedWarzoneOccupation
                {
                    SystemId = SystemId,
                    Occupier = MilitiaFaction.Minmatar,
                    VictoryPoints = 30_000,
                }
            );
            action.Add(
                new SavedMilitiaComplex
                {
                    Slot = slot,
                    Occupier = MilitiaFaction.Minmatar,
                    Progress = -149,
                }
            );
            action.Commit();
        }
        SpacePosition position = zones[1].Position;
        fixture.Store.SetPosition(defender.ShipId, position, SystemId);
        var contact = new MilitiaComplexPilot(defender.PilotId, defender.ShipId, position);
        complexes.Advance(SystemId, [contact], clock.Now);
        var point = complexes
            .Sites(SystemId, clock.Now)
            .Single(candidate => candidate.Id == site.Id)
            .Objects.Single(body => body.Id == zones[1].Id);
        var fields = BlueRead.Fields(Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(new StaticSlimItem(point)))).State);
        Assert.Equal(new object?[] { 500003L, 500002L }, BlueRead.Tuple(fields["component_fwCapturePoint_factionInfo"], 2));
        Assert.Equal(clock.Now + MilitiaBattlefieldRules.PointInterval.Ticks, fields["component_fwCapturePoint_nextPointTime"]);
        for (int second = 0; second < 20; second++)
        {
            clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            complexes.Advance(SystemId, [contact], clock.Now);
        }
        complexes.Advance(SystemId, [contact], clock.Now);
        var warzones = fixture.Services.GetRequiredService<IFactionWarzones>();
        Assert.Equal(27_400, warzones.System(SystemId)!.VictoryPoints);
        Assert.Equal(15_000, warzones.Advantage(SystemId, clock.Now)!.Second.Contribution);
        Assert.Equal(
            150_000,
            live.Economy.Inspect(world =>
                world.Set<SavedLoyaltyBalance>().Find(defender.PilotId, MilitiaRules.TribalLiberationForce)!.Points
            )
        );
        Assert.DoesNotContain(complexes.Sites(SystemId, clock.Now), candidate => candidate.Id == site.Id);

        long due = clock.Now + MilitiaBattlefieldRules.Replacement.Ticks;
        await live.FlushAsync();
        fixture.Services.Dispose();
        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        complexes = reopened.GetRequiredService<IMilitiaComplexes>();
        Assert.DoesNotContain(complexes.Sites(SystemId, due - 1), candidate => candidate.DungeonId == BattlefieldDungeon);
        Assert.NotEqual(site.Id, Assert.Single(complexes.Sites(SystemId, due), candidate => candidate.DungeonId == BattlefieldDungeon).Id);
    }

    private static void LoadBattlefield(WorldDatabase database)
    {
        using CatalogDbContext catalog = new(database.CatalogPath, writable: true);
        string sql = """
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Radius) VALUES
                (75922,'Large Navy Military Beacon',310,0),(73444,'Capture A',922,0),(73856,'Capture B',922,0),
                (73857,'Capture C',922,0),(73448,'Scoreboard',922,0);
            INSERT OR REPLACE INTO SourceRecord(Dataset,Key,Data) VALUES
                ('client/typelist','514','{"includedCategoryIDs":[6]}'),
                ('client/typelist','521','{"includedCategoryIDs":[6]}'),
                ('client/dungeons','9899','{"archetypeID":68,"dungeonNameID":638494,"difficulty":4,
                    "connections":[{"fromObjectID":1,"allowedShipsList":514,"disallowedFittedTypesList":984}],
                    "rooms":{"1":{"objects":{"1":{"typeID":17831,"objectID":1,"position":{"x":0,"y":0,"z":15000}}}},
                        "2":{"objects":{"2":{"typeID":75922,"objectID":2},"3":{"typeID":73444,"objectID":3,"position":{"x":-100000}},
                        "4":{"typeID":73856,"objectID":4,"position":{"y":30000}},
                        "5":{"typeID":73857,"objectID":5,"position":{"x":100000}},"6":{"typeID":73448,"objectID":6}}}}}');
            """;
        catalog.Database.ExecuteSqlRaw(sql.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal));
    }
}
