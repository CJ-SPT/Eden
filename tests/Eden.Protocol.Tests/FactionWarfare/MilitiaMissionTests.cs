using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.FactionWarfare;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Tests.Agents;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.FactionWarfare;

public sealed class MilitiaMissionTests
{
    private const int TargetSystem = 30002087;
    private const int MissionId = 13569;
    private const int ObjectiveType = 92246;
    private const int BeaconType = 75918;
    private const long PilotId = MissionFixture.PilotId;
    private const long AgentId = MissionFixture.AgentId;

    [Fact]
    public void EnlistmentOpensEnemyMissionAndObjectivePaysOnceWithoutNpcStandingLoss()
    {
        using MissionFixture fixture = new(live: true, configure: Configure);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        const MilitiaFaction faction = MilitiaFaction.Amarr;
        int issuer = MilitiaRules.Corporation(faction);
        Assert.False(fixture.Missions.Access(PilotId, AgentId).Allowed);
        using (var action = live.Economy.Begin(new(pilots: [PilotId])))
        {
            action.Add(new SavedMilitiaEnlistment { PilotId = PilotId, DirectFaction = faction });
            action.Commit();
        }

        Assert.True(fixture.Missions.Access(PilotId, AgentId).Allowed);
        var offer = fixture.Request();
        Assert.Equal(MissionId, offer.MissionId);
        Assert.Equal(TargetSystem, offer.SystemId);
        var destinationPlanet = new SpacePosition(2e12, 3e11, -1e12);
        Assert.InRange(offer.Position.DistanceTo(destinationPlanet), 0, WarpCapacitorRules.AstronomicalUnit / 10);
        Assert.Equal((int)MilitiaFaction.Minmatar, offer.EnemyFactionId);
        Assert.True(offer.HasShipRestrictions);
        Assert.Empty(fixture.Missions.Sites(TargetSystem));
        var mission = fixture.Accept(offer);
        var lobby = Assert.Single(fixture.Missions.Sites(TargetSystem), site => site.PublicBeacon is not null);
        Assert.Equal(BeaconType, lobby.PublicBeacon!.TypeId);
        Assert.Equal(mission.Position, lobby.PublicBeacon.Position);
        Assert.True(lobby.PublicBeacon.IsGlobal);
        var gate = Assert.Single(lobby.Gates);
        InventoryItem hull = new(1, 587, 2, 3, 0, 1, 25, (int)InventoryCategory.Ship, 1);
        MissionGateAccessRules.ValidateShip(gate.Access, hull, 0, []);
        Assert.Throws<InvalidDataException>(() =>
            MissionGateAccessRules.ValidateShip(gate.Access, hull with { TypeId = 670, GroupId = 29 }, 0, [])
        );
        Assert.Throws<InvalidDataException>(() => fixture.Missions.Act(PilotId, AgentId, MissionAction.Complete, mission.Id));

        fixture.Missions.Arrive(TargetSystem, gate.Destination);
        long boss = live.Economy.Inspect(world =>
            Assert.Single(world.Set<SavedMissionMember>(), row => row.MissionId == mission.Id && row.CompletesMission).NpcId
        );
        fixture.Kill(boss, commit: false);
        Assert.False(live.Economy.Inspect(world => world.Set<SavedMission>().Find(mission.Id)!.ObjectiveComplete));
        fixture.Kill(boss);
        Assert.True(live.Economy.Inspect(world => world.Set<SavedMission>().Find(mission.Id)!.ObjectiveComplete));
        Assert.Empty(live.Economy.Inspect(world => world.Set<SavedStandingTransaction>().ToArray()));

        var completion = fixture.Missions.Act(PilotId, AgentId, MissionAction.Complete, mission.Id);
        Assert.Equal(mission.Rewards.LoyaltyPoints, completion.PointsAfter);
        Assert.Contains(completion.Standings, change => change.SourceId == issuer && change.After > change.Before);
        Assert.Empty(fixture.Missions.Sites(TargetSystem));
        Assert.Throws<InvalidDataException>(() => fixture.Missions.Act(PilotId, AgentId, MissionAction.Complete, mission.Id));
        live.Economy.Inspect(world =>
        {
            Assert.Empty(world.Set<SavedMissionProgress>());
            Assert.Equal(mission.Rewards.LoyaltyPoints, world.Set<SavedLoyaltyBalance>().Find(PilotId, issuer)!.Points);
            var award = Assert.Single(world.Set<SavedMilitiaLoyaltyAward>());
            Assert.Equal((MilitiaLoyaltySource.Mission, mission.Id), (award.Source, award.SourceId));
            Assert.Equal(
                StandingEvent.AgentMissionCompleted,
                Assert.Single(world.Set<SavedStandingTransaction>(), row => row.SourceId == issuer).Event
            );
            return true;
        });
        Assert.Equal(default, fixture.Services.GetRequiredService<IMilitiaProgression>().Statistics(PilotId, MilitiaStatisticOwner.Pilot));
    }

    [Fact]
    public void DecliningAndExpiringCountOnlyOncePerMission()
    {
        using MissionFixture fixture = new(live: true, configure: Configure);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        const MilitiaFaction faction = MilitiaFaction.Amarr;
        int issuer = MilitiaRules.Corporation(faction);
        using (var action = live.Economy.Begin(new(pilots: [PilotId])))
        {
            action.Add(new SavedMilitiaEnlistment { PilotId = PilotId, DirectFaction = faction });
            action.Commit();
        }

        Decline(fixture.Request());
        Assert.Empty(StandingEvents());
        fixture.Clock.Seconds = 60;
        Decline(fixture.Request());
        Assert.Equal(new[] { StandingEvent.AgentMissionDeclined }, StandingEvents());
        fixture.Clock.Seconds = 4 * 60 * 60;
        Decline(fixture.Request());
        Assert.Equal(new[] { StandingEvent.AgentMissionDeclined }, StandingEvents());

        var mission = fixture.Accept(fixture.Request());
        fixture.Clock.UtcNow = DateTimeOffset.FromFileTime(mission.ExpiresAt);
        Assert.Empty(fixture.Missions.Sites(TargetSystem));
        Assert.Empty(fixture.Missions.Sites(TargetSystem));
        Assert.Equal(new[] { StandingEvent.AgentMissionDeclined, StandingEvent.AgentMissionFailed }, StandingEvents());
        Assert.Empty(live.Economy.Inspect(world => world.Set<SavedMilitiaLoyaltyAward>().ToArray()));

        void Decline(MissionOffer offer) => fixture.Missions.Act(PilotId, AgentId, MissionAction.Decline, offer.Id);
        StandingEvent[] StandingEvents() =>
            live.Economy.Inspect(world =>
                world
                    .Set<SavedStandingTransaction>()
                    .Where(row => row.SourceId == issuer)
                    .OrderBy(row => row.Time)
                    .Select(row => row.Event)
                    .ToArray()
            );
    }

    private static void Configure(CatalogDbContext catalog)
    {
        MissionFixture.Agent(catalog, AgentId, AgentType.FactionalWarfare, 1, MissionRules.SecurityDivision);
        string amarr = """{"factionID":500003}""";
        catalog.Database.ExecuteSql(
            $"UPDATE SourceRecord SET Data={amarr} WHERE Dataset='client/npccorporations' AND Key={AgentTestData.Corporation.ToString()}"
        );
        catalog.Database.ExecuteSql(
            $"""
            INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES ({TargetSystem},20000020,'Militia mission destination');
            INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES
                (50010001,30000142,50010002),(50010002,{TargetSystem},50010001);
            INSERT OR IGNORE INTO ItemCategory(Id,Name) VALUES (2,'Celestial'),(11,'Entity');
            INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES
                (7,'Planet',2),(310,'Beacon',2),(366,'Warp Gate',2),(1186,'FW NPC',11),(186,'Wreck',2);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Radius) VALUES
                (10124,'Arrival beacon',310,0),(17831,'Acceleration Gate',366,500),({BeaconType},'Small beacon',310,0),
                (11,'Planet',7,6000000),(92245,'Republic escort frigate',1186,10),(92244,'Republic escort cruiser',1186,20),
                ({ObjectiveType},'Republic commander',1186,20),(26973,'Wreck',186,20);
            INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z) VALUES
                (40000007,{TargetSystem},11,7,6000000,2e12,3e11,-1e12);
            INSERT OR IGNORE INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES
                ({ObjectiveType},9,100),({ObjectiveType},263,100),({ObjectiveType},265,100);
            """
        );
        Add("client/types", "92246", """{"wreckTypeID":26973}""");
        Add("client/typelist", "512", """{"includedGroupIDs":[25]}""");
        Add(
            "client/missions",
            "13569",
            """
            {"agentTypeID":9,"factionID":500003,"expirationTime":720,"killMission":{"dungeonID":2837,"objectiveQuantity":0}}
            """
        );
        Add(
            "client/dungeons",
            "2837",
            """
            {"entryObjectID":894181,"factionID":500002,"difficulty":1,
              "connections":[{"fromObjectID":894182,"allowedShipsList":512}],
              "rooms":{"20352":{"objects":{
                "894181":{"objectID":894181,"typeID":10124,"position":{"x":0,"y":0,"z":0}},
                "894182":{"objectID":894182,"typeID":17831,"position":{"x":5000,"y":0,"z":0}}
              }},"20353":{"objects":{
                "894183":{"objectID":894183,"typeID":10124,"position":{"x":0,"y":0,"z":0}}
              }}}}
            """
        );

        void Add(string dataset, string key, string value) =>
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ({dataset},{key},{value})");
    }
}
