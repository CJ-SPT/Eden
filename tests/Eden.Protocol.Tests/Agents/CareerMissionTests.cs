using Eden.Data.World.Agents;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class CareerMissionTests
{
    private const long Pilot = MissionFixture.PilotId;
    private const long Soldier = 3019471;
    private const int Station = 60015001;
    private const int Ibis = 601;
    private const int CaldariFrigate = 3330;

    [Fact]
    public void EveryPinnedCareerAgentBindsItsCompleteChain()
    {
        using var fixture = Create();
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath);
        var agents = fixture.Services.GetRequiredService<INpcDirectory>().Agents().Where(a => a.Type == AgentType.Career).ToArray();
        Assert.Equal(100, agents.Length);
        HashSet<int> definitions = [];
        foreach (var agent in agents)
        {
            var chain = CareerMissionCatalog.ForAgent(catalog, agent);
            Assert.Equal(agent.DivisionId == 26 ? 5 : 10, chain.Count);
            var root = Assert.Single(chain, part => part.Chain!.PreviousMissionId == 0);
            var profile = MissionCatalog.Find(catalog, root.MissionId);
            Assert.True(MissionCatalog.ChainAvailable(catalog, profile, agent));
            Assert.True(fixture.Missions.Information(Pilot, agent.Id).MissionAvailable);
            definitions.UnionWith(chain.Select(part => part.MissionId));
        }
        Assert.Equal(180, definitions.Count);
    }

    [Fact]
    public async Task SoldierOffersTheNextStageOnceAndSettlesTheMissionShipLossAcrossCheckpoint()
    {
        using var fixture = Create();
        var live = LiveWorld.Find(fixture.Db.Path)!;
        var missions = fixture.Missions;
        Move(live, Station);
        var last = MissionFixture.Reply(fixture.Call("agentMgr", "GetCompletedCareerAgentIDs", [new List<object?> { Soldier }]));
        Assert.False(Assert.IsType<bool>(Assert.IsType<Dictionary<long, object?>>(last)[Soldier]));
        var root = fixture.Request(Soldier);
        Assert.True(root.NonExpiring);
        Assert.Equal(15104, root.MissionId);
        var accepted = fixture.Accept(root);
        Assert.Equal(long.MaxValue, accepted.ExpiresAt);
        string service = $"@agent:{Soldier}";
        var briefing = BlueRead.Fields(MissionFixture.Reply(fixture.Call(service, "GetMissionBriefingInfo", [])));
        Assert.Null(briefing["Expiration Time"]);
        var details = BlueRead.Fields(MissionFixture.Reply(fixture.Call(service, "GetMissionJournalInfo", [Pilot, (long)root.MissionId])));
        Assert.Null(details["expirationTime"]);
        var target = missions.Sites(root.SystemId).Single(s => s.Id == root.Id && s.Started == false);
        missions.Arrive(root.SystemId, target.Position);
        for (int wave = 1; wave <= 3; wave++)
        {
            var enemies = live.Economy.Inspect(world =>
                world
                    .Filter<Eden.Data.World.Combat.Npcs.WorldNpc>(npc =>
                        npc.EncounterId == root.Id + MissionRules.FirstSiteId && npc.DestroyedAt == null
                    )
                    .Select(npc => npc.Id)
                    .ToArray()
            );
            Assert.Equal(wave, enemies.Length);
            foreach (long enemy in enemies)
            {
                fixture.Kill(enemy);
            }
        }
        var first = missions.Act(Pilot, Soldier, MissionAction.Complete, root.Id);
        var second = Assert.IsType<MissionOffer>(first.Continuation);
        Assert.Equal(15108, second.MissionId);
        Assert.Throws<InvalidDataException>(() => missions.Act(Pilot, Soldier, MissionAction.Complete, root.Id));
        var gift = Assert.Single(missions.Act(Pilot, Soldier, MissionAction.Accept, second.Id).Items);
        Assert.Equal(second.CareerGift!.TypeId, gift.Item.TypeId);
        Assert.Throws<InvalidDataException>(() => missions.Act(Pilot, Soldier, MissionAction.Accept, second.Id));
        var fleet = fixture.Services.GetRequiredService<IPilotFleet>();
        long sacrificial = Assert.Single(fleet.Assemble(Pilot, [gift.Item.Id], null).ShipIds);
        long original = live.Economy.Inspect(world => world.Pilots.Find(Pilot)!.ActiveShipId);
        using (var world = live.Economy.Begin(new(pilots: [Pilot])))
        {
            var skill =
                world.Set<PilotSkill>().Find(Pilot, CaldariFrigate)
                ?? world.Add(new PilotSkill { PilotId = Pilot, TypeId = CaldariFrigate }).Entity;
            skill.Points = Math.Max(skill.Points, 500);
            world.Commit();
        }
        fleet.Board(Pilot, sacrificial, original);
        var objective = missions.Sites(second.SystemId).Single(s => s.Id == second.Id && s.CareerVisit is not null);
        Move(live, Station, second.SystemId, objective.CareerVisit!.Position);
        Assert.True(missions.VisitCareerSite(Pilot, second.SystemId, second.Id));
        Assert.True(missions.Current(Pilot, Soldier)!.ObjectiveComplete);
        live.Economy.Inspect(world =>
        {
            var loss = world.Set<ShipLoss>().Find(sacrificial)!;
            Assert.Equal(Pilot, loss.PilotId);
            Assert.Equal(loss.CapsuleId, world.Pilots.Find(Pilot)!.ActiveShipId);
            return true;
        });
        await live.FlushAsync();
        fixture.StopResident();
        using var reopened = ServerServices.Build(new(fixture.Db.Path), fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<IPilotMissions>();
        Assert.True(restored.Current(Pilot, Soldier)!.ObjectiveComplete);
        live = LiveWorld.Find(fixture.Db.Path)!;
        Move(live, Station);
        Assert.Equal(15112, restored.Act(Pilot, Soldier, MissionAction.Complete, second.Id).Continuation!.MissionId);
        Assert.Empty(restored.CompletedCareerAgents(Pilot, new HashSet<long> { Soldier }));
    }

    [Fact]
    public void SoldierModuleLessonsRequireTheMatchingTargetAndPaidRepairCycle()
    {
        using var fixture = Create();
        var live = LiveWorld.Find(fixture.Db.Path)!;
        Move(live, Station);
        SeedCompletedStages(live, fixture.Clock.Now, 15104, 15108);
        var tackle = fixture.Request(Soldier);
        long disruptor = AcceptAndFit(tackle, InventoryFlag.MediumSlot0);
        var pirate = VisitTarget(tackle, 2010);
        var tackleStore = fixture.Services.GetRequiredService<IPilotTackle>();
        Assert.Throws<InvalidDataException>(() =>
            tackleStore.Activate(Pilot, disruptor, DogmaEffect.WarpDisruptor, pirate, new HashSet<long>(), fixture.Clock.Now)
        );
        _ = tackleStore.Activate(Pilot, disruptor, DogmaEffect.WarpDisruptor, pirate, new HashSet<long> { pirate.Id }, fixture.Clock.Now);
        Assert.True(fixture.Missions.Current(Pilot, Soldier)!.ObjectiveComplete);

        Move(live, Station);
        var repair = fixture.Missions.Act(Pilot, Soldier, MissionAction.Complete, tackle.Id).Continuation!;
        Assert.Equal(15115, repair.MissionId);
        long repairer = AcceptAndFit(repair, InventoryFlag.HighSlot0);
        var vessel = VisitTarget(repair, 32471);
        var repairStore = fixture.Services.GetRequiredService<IPilotRemoteRepairing>();
        var activation = repairStore.Start(
            Pilot,
            repairer,
            DogmaEffect.RemoteShieldBoost,
            vessel.Id,
            new HashSet<long> { vessel.Id },
            fixture.Clock.Now
        );
        Assert.True(activation.Cycle.CareerTarget);
        repairStore.CompleteCareer(Pilot, activation.Cycle, activation.Cycle.CompletesAt - 1);
        Assert.False(fixture.Missions.Current(Pilot, Soldier)!.ObjectiveComplete);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(activation.Cycle.CompletesAt - fixture.Clock.Now));
        repairStore.CompleteCareer(Pilot, activation.Cycle, fixture.Clock.Now);
        Assert.True(fixture.Missions.Current(Pilot, Soldier)!.ObjectiveComplete);

        long AcceptAndFit(MissionOffer offer, InventoryFlag flag)
        {
            long id = Assert.Single(fixture.Missions.Act(Pilot, Soldier, MissionAction.Accept, offer.Id).Items).Item.Id;
            using var world = live.Economy.Begin(new(pilots: [Pilot]));
            var module = world.Set<OwnedItem>().Find(id)!;
            module.LocationId = world.Pilots.Find(Pilot)!.ActiveShipId;
            module.Singleton = true;
            module.Online = true;
            module.FlagId = (int)flag;
            world.Commit();
            return id;
        }

        TackleTarget VisitTarget(MissionOffer offer, int type)
        {
            var site = Assert.Single(fixture.Missions.Sites(offer.SystemId), s => s.Id == offer.Id);
            fixture.Missions.Arrive(offer.SystemId, site.Position);
            var npc = live.Economy.Inspect(world =>
                Assert.Single(world.Filter<WorldNpc>(n => n.EncounterId == MissionRules.FirstSiteId + offer.Id && n.TypeId == type))
            );
            Move(live, Station, offer.SystemId, new SpacePosition(npc.X + 500, npc.Y, npc.Z));
            return new(npc.Id, TackleTargetKind.Npc);
        }
    }

    [Fact]
    public void SoldierRetriesTheFailedStageAfterDowntimeWithItsCompletedPredecessor()
    {
        using var fixture = Create();
        var live = LiveWorld.Find(fixture.Db.Path)!;
        Move(live, Station);
        SeedCompletedStages(live, fixture.Clock.Now, 15104, 15108);
        var offer = fixture.Accept(fixture.Request(Soldier));
        var site = Assert.Single(fixture.Missions.Sites(offer.SystemId), site => site.Id == offer.Id);
        fixture.Missions.Arrive(offer.SystemId, site.Position);
        var target = live.Economy.Inspect(world =>
            Assert.Single(world.Filter<WorldNpc>(npc => npc.EncounterId == MissionRules.FirstSiteId + offer.Id))
        );

        fixture.Kill(target.Id);
        Assert.Equal(MissionState.Failed, fixture.Missions.Current(Pilot, Soldier)!.State);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(659));
        Assert.False(fixture.Missions.Access(Pilot, Soldier).Allowed);
        Assert.Throws<InvalidDataException>(() => fixture.Request(Soldier));

        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
        var retry = fixture.Request(Soldier);
        Assert.NotEqual(offer.Id, retry.Id);
        Assert.Equal(offer.MissionId, retry.MissionId);
        Assert.Equal(MissionState.Accepted, fixture.Accept(retry).State);
    }

    private static void SeedCompletedStages(LiveWorld live, long now, params int[] stages)
    {
        using var world = live.Economy.Begin(new(pilots: [Pilot]));
        foreach (int stage in stages)
        {
            world.Add(
                new SavedMission
                {
                    PilotId = Pilot,
                    AgentId = Soldier,
                    MissionId = stage,
                    Level = 1,
                    State = MissionState.Completed,
                    OfferedAt = now,
                    AcceptedAt = now,
                    ClosedAt = now,
                    ExpiresAt = long.MaxValue,
                    DestinationStation = Station,
                    SystemId = 30001389,
                    ObjectiveComplete = true,
                }
            );
        }
        world.Commit();
    }

    private static MissionFixture Create() =>
        new(
            live: true,
            configure: catalog =>
            {
                string path = catalog.Database.GetDbConnection().DataSource;
                PassiveCatalogFixture.LoadCareerMissions(path);
                PassiveCatalogFixture.LoadSmallHulls(path, new HashSet<int> { Ibis });
            }
        );

    private static void Move(LiveWorld live, int station, int? system = null, SpacePosition? position = null)
    {
        using var world = live.Economy.Begin(new(pilots: [Pilot]));
        var ship = world.Pilots.Find(Pilot)!.ActiveShip;
        ship.StationId = station;
        if (position is { } point)
        {
            var saved = world.Positions.Find(ship.Id) ?? world.Add(new Eden.Data.World.Docking.FlightPosition { ShipId = ship.Id }).Entity;
            saved.SystemId = system;
            saved.X = point.X;
            saved.Y = point.Y;
            saved.Z = point.Z;
        }
        else if (world.Positions.Find(ship.Id) is { } current)
        {
            world.Remove(current);
        }
        world.Commit();
    }
}
