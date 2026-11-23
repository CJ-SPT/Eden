using Eden.Data.World.Agents;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Docking;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.World.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class EpicArcHackingTests
{
    private const int Miscommunication = 15055;
    private const long Agent = 3019458;
    private const int System = 30000142;
    private const int DataAnalyzer = 22175;
    private const long Pilot = MissionFixture.PilotId;

    [Fact]
    public async Task MiscommunicationHackCompletesWithoutLootAndSpawnsReinforcementsOnceAcrossCheckpoint()
    {
        using MissionFixture fixture = new(
            live: true,
            configure: catalog =>
            {
                MissionFixture.Agent(catalog, Agent, AgentType.EpicArc, 3, MissionRules.SecurityDivision);
                PassiveCatalogFixture.LoadHacking(catalog.Database.GetDbConnection().DataSource);
                // Synthetic geometry for the authored IDs, with the production analyzer transaction.
                const string mission = """
                    {"contentTemplate":"agent.missionTemplatizedContent_EpicArcKillMission","killMission":{"dungeonID":4095}}
                    """;
                const string dungeon = """
                    {"entryObjectID":963180,"connections":[{"fromObjectID":963181}],"rooms":{
                        "22118":{"objects":{
                            "963180":{"objectID":963180,"typeID":10124,"position":{"x":0,"y":0,"z":0}},
                            "963181":{"objectID":963181,"typeID":17831,"position":{"x":500,"y":0,"z":0}}}},
                        "22076":{"objects":{
                            "961943":{"objectID":961943,"typeID":10124,"position":{"x":0,"y":0,"z":0}},
                            "961995":{"objectID":961995,"typeID":32377,"position":{"x":1000,"y":0,"z":0}}}}}}
                    """;
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT OR IGNORE INTO ItemCategory(Id,Name) VALUES (11,'Entity');
                    INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES
                        (366,'Test acceleration gate',2),(671,'Test NPC frigate',11),(676,'Test NPC destroyer',11);
                    INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Radius) VALUES
                        (10124,'Test entry',12,1),(17831,'Test gate',366,1),(32377,'Test tower',12,3),
                        (25015,'Test scout',671,10),(25026,'Test destroyer',676,10),(25037,'Test reinforcement',671,10);
                    INSERT OR REPLACE INTO SourceRecord VALUES ('client/missions','15055',{0});
                    INSERT OR REPLACE INTO SourceRecord VALUES ('client/dungeons','4095',{1});
                    INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','32377',{2});
                    """,
                    mission,
                    dungeon,
                    """{"volume":1}"""
                );
            }
        );
        var live = LiveWorld.Find(fixture.Db.Path)!;
        long missionId;
        long moduleId;
        long targetId;
        using (var world = live.Economy.Begin(new(pilots: [Pilot])))
        {
            long ship = world.Pilots.Find(Pilot)!.ActiveShipId;
            var row = world
                .Add(
                    new SavedMission
                    {
                        PilotId = Pilot,
                        AgentId = Agent,
                        MissionId = Miscommunication,
                        Level = 3,
                        State = MissionState.Accepted,
                        OfferedAt = fixture.Clock.Now,
                        AcceptedAt = fixture.Clock.Now,
                        ExpiresAt = long.MaxValue,
                        DestinationStation = MissionFixture.StationId,
                        SystemId = System,
                    }
                )
                .Entity;
            missionId = row.Id;
            targetId = MissionContainerRules.Id(missionId, 0);
            world.Add(
                new SavedHackingContainer
                {
                    Id = targetId,
                    SiteId = MissionRules.FirstSiteId + missionId,
                    OwnerId = Pilot,
                    LootSeed = 129,
                }
            );
            var position = world.Positions.Find(ship) ?? world.Add(new FlightPosition { ShipId = ship }).Entity;
            position.SystemId = System;
            position.X = MissionGateRules.RoomSpacing + 1000;
            var skill =
                world.Set<PilotSkill>().Find(Pilot, HackingRules.HackingSkill)
                ?? world.Add(new PilotSkill { PilotId = Pilot, TypeId = HackingRules.HackingSkill }).Entity;
            skill.Points = Math.Max(skill.Points, 1000);
            moduleId = world.NextItemId();
            world.Add(
                new OwnedItem
                {
                    Id = moduleId,
                    OwnerId = Pilot,
                    LocationId = ship,
                    FlagId = (int)InventoryFlag.MediumSlot0,
                    TypeId = DataAnalyzer,
                    Quantity = 1,
                    Singleton = true,
                    Online = true,
                }
            );
            world.Commit();
        }

        var missions = fixture.Missions;
        var room = Assert.Single(missions.Sites(System), site => site.RoomId == 22076);
        missions.Arrive(System, room.Position);
        Assert.False(missions.Current(Pilot, Agent)!.ObjectiveComplete);
        var hacking = fixture.Services.GetRequiredService<IPilotHacking>();
        var locks = new HashSet<long> { targetId };
        var board = hacking.Begin(Pilot, moduleId, targetId, locks, fixture.Clock.Now).Board;
        while (board.Outcome == HackingOutcome.Running)
        {
            var core = board.Tiles.Single(tile => tile.Type == HackingNode.Core);
            var tile = core.Hidden ? board.Tiles.First(tile => tile.Hidden && HackingRules.Accessible(board.Tiles, tile)) : core;
            board = hacking.Act(Pilot, new(HackingActionKind.Click, tile.Coordinate), fixture.Clock.Now).Current;
        }

        Assert.Equal(HackingOutcome.Won, board.Outcome);
        Assert.True(missions.Current(Pilot, Agent)!.ObjectiveComplete);
        Assert.Empty(fixture.Services.GetRequiredService<IPilotMissionContainers>().Read(Pilot, targetId).Items);
        Assert.Throws<InvalidDataException>(() => hacking.Begin(Pilot, moduleId, targetId, locks, fixture.Clock.Now));
        await live.FlushAsync();
        fixture.StopResident();

        using var reopened = ServerServices.Build(new(fixture.Db.Path), fixture.Clock, liveWorld: true);
        var restored = LiveWorld.Find(fixture.Db.Path)!;
        Assert.True(reopened.GetRequiredService<IPilotMissions>().Current(Pilot, Agent)!.ObjectiveComplete);
        Assert.Equal(
            2,
            restored.Economy.Inspect(world =>
                world.Filter<WorldNpc>(npc => npc.EncounterId == MissionRules.FirstSiteId + missionId && npc.TypeId == 25037).Count()
            )
        );
    }
}
