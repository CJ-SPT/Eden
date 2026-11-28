using System.Text.Json;
using Eden.Data.World.Agents;
using Eden.Data.World.Docking;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;
using Eden.Protocol.Serialization;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class EpicArcCourierTests
{
    private const long Pilot = MissionFixture.PilotId;
    private const long Agent = 3019400;
    private const int Arc = 40;
    private const int Choice = 14969;
    private const int HomeInPeace = 14973;
    private const int PickupStation = 60015124;
    private const int DestinationStation = 60002065;
    private const int AgentSystem = 30000142;
    private const int DestinationSystem = 30000144;

    [Fact]
    public void HomeInPeaceRoutesToTheDeliveryStationForNewAndExistingOffers()
    {
        using MissionFixture fixture = new(live: true, configure: ConfigurePenumbra);
        var live = LiveWorld.Find(fixture.Db.Path)!;
        var agent = fixture.Services.GetRequiredService<INpcDirectory>().Agent(Agent)!.Space!;
        var destination = new SpacePosition(123, 456, 789);
        long choice;
        using (var world = live.Economy.Begin(new(pilots: [Pilot])))
        {
            long ship = world.Pilots.Find(Pilot)!.ActiveShipId;
            var position = world.Positions.Find(ship) ?? world.Add(new FlightPosition { ShipId = ship }).Entity;
            position.SystemId = agent.SystemId;
            position.X = agent.Position.X;
            position.Y = agent.Position.Y;
            position.Z = agent.Position.Z;
            choice = world
                .Add(
                    new SavedMission
                    {
                        PilotId = Pilot,
                        AgentId = Agent,
                        MissionId = Choice,
                        Level = 4,
                        State = MissionState.Accepted,
                        OfferedAt = fixture.Clock.Now,
                        AcceptedAt = fixture.Clock.Now,
                        ExpiresAt = long.MaxValue,
                        SystemId = AgentSystem,
                    }
                )
                .Entity.Id;
            world.Commit();
        }

        var offer = fixture.Missions.ChooseEpicArc(Pilot, Agent, choice, HomeInPeace).Continuation!;
        Assert.Equal(DestinationSystem, offer.SystemId);
        Assert.Equal(destination, offer.Position);
        Assert.Equal(PickupStation, offer.Cargo!.PickupStation);
        Assert.Equal(DestinationStation, offer.Cargo.DestinationStation);
        fixture.Accept(offer);

        using (var world = live.Economy.Begin(new(pilots: [Pilot])))
        {
            var row = world.Set<SavedMission>().Find(offer.Id)!;
            Assert.Equal(DestinationSystem, row.SystemId);
            Assert.Equal(destination, new SpacePosition(row.X, row.Y, row.Z));
            // Older offers persisted the space agent's location in all four fields.
            row.SystemId = agent.SystemId;
            row.X = agent.Position.X;
            row.Y = agent.Position.Y;
            row.Z = agent.Position.Z;
            world.Commit();
        }

        var restored = fixture.Missions.Current(Pilot, Agent)!;
        Assert.Equal(DestinationSystem, restored.SystemId);
        Assert.Equal(destination, restored.Position);
        var journal = BlueRead.Tuple(MissionFixture.Reply(fixture.Call("agentMgr", "GetMyJournalDetails", [])), 2);
        var entry = BlueRead.Tuple(Assert.Single(Assert.IsType<List<object?>>(journal[0])), 10);
        var bookmarks = Assert
            .IsType<List<object?>>(entry[6])
            .Select(bookmark => BlueRead.Fields(Assert.IsType<BlueInstance>(bookmark).State));
        var dropOff = Assert.Single(bookmarks, bookmark => BlueMarshal.Text(bookmark["locationType"]) == "objective.destination");
        Assert.Equal((long)DestinationStation, dropOff["itemID"]);
        Assert.Equal((long)DestinationSystem, dropOff["solarsystemID"]);
    }

    private static void ConfigurePenumbra(CatalogDbContext catalog)
    {
        MissionFixture.Agent(catalog, Agent, AgentType.EpicArc, 4, MissionRules.SecurityDivision);
        // All pinned assignments are needed to load the graph. The test exercises one local branch.
        int[] stages =
        [
            14490,
            14492,
            14511,
            14548,
            14552,
            14553,
            14558,
            14559,
            14576,
            14597,
            14599,
            14601,
            14602,
            14603,
            14607,
            14610,
            14612,
            14613,
            14616,
            14619,
            14827,
            14833,
            14847,
            14848,
            14849,
            14882,
            14884,
            14885,
            14886,
            14898,
            14900,
            14901,
            14903,
            Choice,
            14970,
            14971,
            14972,
            HomeInPeace,
            14975,
            14976,
        ];
        var graph = stages.ToDictionary(
            id => id,
            id => new
            {
                agentID = EpicArcCatalog.Find(id)!.FixedAgentIds.Single(),
                nextMissions = id == Choice ? new[] { 14971, HomeInPeace } : [],
            }
        );
        string rawGraph = JsonSerializer.Serialize(new { arcRestartInterval = 129600, missions = graph });
        const string rawChoice = """{"contentTemplate":"agent.missionTemplatizedContent_EpicArcAgentInteractionMission"}""";
        const string rawCourier = """
            {"contentTemplate":"agent.missionTemplatizedContent_EpicArcCourierMission",
             "courierMission":{"objectiveTypeID":30980,"objectiveQuantity":10000,"objectiveSingleton":0}}
            """;
        const string rawContact = """{"typeID":30992,"solarSystemID":30000142,"dungeonID":3876}""";
        const string rawDungeon = """
            {"dungeonNameID":4013876,"entryObjectID":1,"rooms":{"1":{"objects":{
                "1":{"objectID":1,"typeID":10124,"position":{"x":0,"y":0,"z":0}},
                "2":{"objectID":2,"typeID":30992,"position":{"x":100,"y":200,"z":300}}}}}}
            """;
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                SELECT 60015124,TypeId,OwnerId,30000142,OperationId,OrbitId,0,0,0 FROM Station WHERE Id=60003760;
            INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                SELECT 60002065,TypeId,OwnerId,30000144,OperationId,OrbitId,123,456,789 FROM Station WHERE Id=60003760;
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId)
                VALUES (10124,'Test beacon',12),(30992,'Test contact',12),(30980,'Test cargo',12);
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','30980','{{"volume":0.5}}');
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/localization_fsd_en-us','4019400','{{"text":"Test contact"}}');
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/localization_fsd_en-us','4013876','{{"text":"Test beacon"}}');
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/epicarcs',{0},{1});
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/missions',{2},{3});
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/missions',{4},{5});
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/agentsinspace',{6},{7});
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/dungeons','3876',{8});
            """,
            Arc,
            rawGraph,
            Choice,
            rawChoice,
            HomeInPeace,
            rawCourier,
            Agent,
            rawContact,
            rawDungeon
        );
    }
}
