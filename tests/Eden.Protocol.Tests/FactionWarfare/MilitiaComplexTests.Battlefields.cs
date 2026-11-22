using System.Globalization;
using Eden.Data.Catalog.FactionWarfare;
using Eden.Data.World.Combat;
using Eden.Data.World.FactionWarfare;
using Eden.Data.World.Market;
using Eden.Gameplay.Combat;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.FactionWarfare;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.FactionWarfare;

public sealed partial class MilitiaComplexTests
{
    private const int BattlefieldDungeon = 9899;

    [Theory]
    [InlineData(MilitiaFaction.Amarr, 9898, 638491, 638492, 638487)]
    [InlineData(MilitiaFaction.Minmatar, 9899, 638500, 638501, 638496)]
    [InlineData(MilitiaFaction.Caldari, 9900, 638510, 638511, 638506)]
    [InlineData(MilitiaFaction.Gallente, 9901, 638519, 638520, 638515)]
    public void BattlefieldCatalogResolvesPinnedRallyBeacons(
        MilitiaFaction occupier,
        int dungeonId,
        int defender,
        int attacker,
        int neutral
    )
    {
        using TestWorld fixture = new();
        WorldDatabase database = new(fixture.Path);
        LoadComplexCatalog(database);
        LoadBattlefield(database, dungeonId, defender, attacker, neutral);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var profile = MilitiaComplexCatalog.Read(catalog.Definitions, MilitiaComplexVariants.Battlefield, occupier);
        Assert.NotNull(profile);
        Assert.Equal(dungeonId, profile.DungeonId);
        Assert.NotNull(profile.Battlefield);
        Assert.Equal(new SpacePosition(0, 0, -90_000), profile.Battlefield.DefenderRally);
        Assert.Equal(new SpacePosition(0, 0, 90_000), profile.Battlefield.AttackerRally);
        Assert.Equal(new SpacePosition(0, -50_000, 0), profile.Battlefield.NeutralRally);
    }

    [Fact]
    public void BattlefieldGateSelectsAffiliationAndRevalidatesBeforeFlagging()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(fixture.Path);
        LoadComplexCatalog(database);
        LoadBattlefield(database);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var complexes = fixture.Services.GetRequiredService<IMilitiaComplexes>();
        var pilot = fixture.Store.FindPilot(FirstAccount)!;
        var site = complexes.Sites(SystemId, clock.Now).Single(candidate => candidate.DungeonId == BattlefieldDungeon);
        var gate = site.Objects.Single(body => body.Id == MilitiaComplexRules.GateId(site.Id));
        var center = site.Objects.Single(body => body.Id == MilitiaComplexRules.BeaconId(site.Id)).Position;
        var defender = center + new SpacePosition(0, 0, -90_000);
        var attacker = center + new SpacePosition(0, 0, 90_000);
        var neutral = center + new SpacePosition(0, -50_000, 0);
        fixture.Store.SetPosition(pilot.ShipId, gate.Position, SystemId);

        SetAffiliation(MilitiaFaction.Minmatar, ShipSafetyLevel.Full);
        Assert.Equal(defender, Preview());
        Assert.Equal(defender, complexes.Gate(pilot.PilotId, gate.Id, clock.Now, defender));
        SetAffiliation(MilitiaFaction.Amarr, ShipSafetyLevel.Full);
        Assert.Equal(attacker, Preview());
        Assert.Equal(attacker, complexes.Gate(pilot.PilotId, gate.Id, clock.Now, attacker));
        SetAffiliation(MilitiaFaction.Caldari, ShipSafetyLevel.Full);
        Assert.Equal(neutral, Preview());
        Assert.Equal(neutral, complexes.Gate(pilot.PilotId, gate.Id, clock.Now, neutral));
        Assert.Equal(gate.Position, complexes.Redirect(SystemId, neutral, clock.Now));

        SetAffiliation(null, ShipSafetyLevel.Full);
        Assert.Throws<InvalidDataException>(() => Preview());
        SetAffiliation(null, ShipSafetyLevel.Partial);
        Assert.Equal(neutral, Preview());
        Assert.Throws<InvalidDataException>(() => complexes.Gate(pilot.PilotId, gate.Id, clock.Now, attacker));
        Assert.Null(live.Economy.Inspect(world => world.Set<SavedCrimewatch>().Find(pilot.PilotId)));
        Assert.Equal(neutral, complexes.Gate(pilot.PilotId, gate.Id, clock.Now, neutral));
        var timers = live.Economy.Inspect(world => world.Set<SavedCrimewatch>().Find(pilot.PilotId)!.Timers());
        Assert.Equal(CrimewatchFlag.Suspect, timers.Flag(clock.Now));

        SpacePosition Preview() => complexes.Gate(pilot.PilotId, gate.Id, clock.Now);

        void SetAffiliation(MilitiaFaction? faction, ShipSafetyLevel safety)
        {
            using var action = live.Economy.Begin(new(pilots: [pilot.PilotId]));
            var enlistment =
                action.Set<SavedMilitiaEnlistment>().Find(pilot.PilotId)
                ?? action.Add(new SavedMilitiaEnlistment { PilotId = pilot.PilotId }).Entity;
            enlistment.DirectFaction = faction;
            action.Pilots.Find(pilot.PilotId)!.SafetyLevel = safety;
            action.Commit();
        }
    }

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

    private static void LoadBattlefield(
        WorldDatabase database,
        int dungeonId = BattlefieldDungeon,
        int defender = 638500,
        int attacker = 638501,
        int neutral = 638496
    )
    {
        using CatalogDbContext catalog = new(database.CatalogPath, writable: true);
        const string hullList = """{"includedCategoryIDs":[6]}""";
        string dungeon = $$"""
            {"archetypeID":68,"dungeonNameID":638494,"difficulty":4,
                "connections":[{"fromObjectID":1,"allowedShipsList":514,"disallowedFittedTypesList":984}],
                "rooms": {
                    "1": {
                        "objects": {"1":{"typeID":17831,"objectID":1,"position":{"x":0,"y":0,"z":15000} } }
                    },
                    "2": {
                        "objects": {
                            "2": {"typeID":75922,"objectID":2},
                            "3": {"typeID":73444,"objectID":3,"position":{"x":-100000} },
                            "4": {"typeID":73856,"objectID":4,"position":{"y":30000} },
                            "5": {"typeID":73857,"objectID":5,"position":{"x":100000} },
                            "6": {"typeID":73448,"objectID":6},
                            "7": {"typeID":10124,"objectID":7,"objectNameID":{{attacker}},"position":{"z":90000} },
                            "8": {"typeID":10124,"objectID":8,"objectNameID":{{neutral}},"position":{"y":-50000} },
                            "9": {"typeID":10124,"objectID":9,"objectNameID":{{defender}},"position":{"z":-90000} }
                        }
                    }
                }
            }
            """;
        catalog.Database.ExecuteSql(
            $"""
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Radius) VALUES
                (10124,'Rally Point',310,0),(75922,'Large Navy Military Beacon',310,0),
                (73444,'Capture A',922,0),(73856,'Capture B',922,0),
                (73857,'Capture C',922,0),(73448,'Scoreboard',922,0);
            INSERT OR REPLACE INTO SourceRecord(Dataset,Key,Data) VALUES
                ('client/typelist','514',{hullList}),
                ('client/typelist','521',{hullList}),
                ('client/dungeons',{dungeonId.ToString(CultureInfo.InvariantCulture)},{dungeon});
            """
        );
    }
}
