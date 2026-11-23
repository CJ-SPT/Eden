using Eden.Data.World.Docking;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Tests.Exploration;
using Eden.Protocol.Tests.Fixtures;
using Eden.World.Agents;
using Eden.World.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class CareerExplorerTests
{
    private const long Pilot = MissionFixture.PilotId;
    private const long Agent = 3019445;
    private const int DataAnalyzer = 27014;
    private const int RelicAnalyzer = 27019;
    private const int AnomalyContainer = 32353;
    private const int DataContainer = 32369;
    private const int RelicContainer = 32367;
    private const int GasContainer = 32379;

    [Fact]
    public async Task ExplorerChainGatesPrivateProofsAndNativeHackingAcrossCheckpoint()
    {
        using MissionFixture f = new(
            live: true,
            configure: catalog => PassiveCatalogFixture.LoadCareerMissions(catalog.Database.GetDbConnection().DataSource)
        );
        var missions = f.Missions;
        var directory = f.Services.GetRequiredService<INpcDirectory>();
        var explorationAgent = directory.Agent(Agent)!;
        int station = explorationAgent.StationId!.Value;
        using (var catalog = new Eden.World.Catalog.CatalogDbContext(new Eden.World.Storage.WorldDatabase(f.Db.Path).CatalogPath))
        {
            var all = directory.Agents().Where(agent => agent.Type == AgentType.Career && agent.DivisionId == 26).ToArray();
            Assert.Equal(20, all.Length);
            Assert.Equal(4, all.Select(agent => agent.FactionId).Distinct().Count());
            Assert.All(all, agent => Assert.Equal(5, CareerMissionCatalog.ForAgent(catalog, agent).Count));
        }
        Dock(f, station);
        var initial = missions.Act(Pilot, Agent, MissionAction.Request).Offer!;
        var anomaly = f.Accept(initial);
        Assert.Equal(14990, anomaly.MissionId);
        Assert.Null(missions.EntryPoint(Pilot, Agent));
        var scanner = Assert.Single(missions.TrainingSites(Pilot, anomaly.SystemId));
        Assert.False(scanner.IsSignature);
        Assert.Empty(missions.TrainingSites(Pilot + 1, anomaly.SystemId));
        Assert.All(missions.Sites(anomaly.SystemId).Where(site => site.Id == anomaly.Id), site => Assert.True(site.Private));
        Recover(f, anomaly, AnomalyContainer);

        var tour = Next(f, anomaly, station, 14991);
        Assert.NotNull(missions.EntryPoint(Pilot, Agent));
        var rooms = missions.Sites(tour.SystemId).Where(site => site.Id == tour.Id).ToArray();
        Assert.Equal(5, rooms.Length);
        Assert.Equal(4, rooms.Sum(room => room.Gates.Count));
        var crate = rooms.SelectMany(room => room.Objects).Single(body => body.TypeId == 32347);
        Move(f, tour.SystemId, crate.Position);
        var pickups = f.Services.GetRequiredService<IPilotMissionContainers>().Read(Pilot, crate.Id).Items;
        Assert.Contains(pickups, item => item is { TypeId: 30013, Quantity: 16 });
        Assert.Contains(pickups, item => item.TypeId == DataAnalyzer);
        Assert.Contains(pickups, item => item.TypeId == RelicAnalyzer);
        Assert.False(missions.VisitCareerSite(Pilot, tour.SystemId, tour.Id));
        Move(f, tour.SystemId, rooms.Single(room => room.CareerVisit is not null).Position);
        Assert.True(missions.VisitCareerSite(Pilot, tour.SystemId, tour.Id));

        var data = Next(f, tour, station, 14995);
        HackProof(f, data, DataContainer, ExplorationKind.Data);
        var relic = Next(f, data, station, 14993);
        HackProof(f, relic, RelicContainer, ExplorationKind.Relic);
        var gas = Next(f, relic, station, 14996);
        var gasSignature = Assert.Single(missions.TrainingSites(Pilot, gas.SystemId));
        Assert.True(gasSignature.IsSignature);
        Resolve(f, gasSignature);
        var gasRooms = missions.Sites(gas.SystemId).Where(site => site.Id == gas.Id).ToArray();
        var gate = Assert.Single(gasRooms.SelectMany(room => room.Gates));
        Assert.Equal(new MissionGateKey(32375, 1, false), gate.Access.Key);
        long ship = Move(f, gas.SystemId, gate.Position);
        Assert.Throws<InvalidDataException>(() => missions.ActivateGate(Pilot, gate.Id, gate.Destination));
        var live = LiveWorld.Find(f.Db.Path)!;
        using (var world = live.Economy.Begin(new(pilots: [Pilot])))
        {
            var key = world.Filter<OwnedItem>(item => item.OwnerId == Pilot && item.LocationId == station && item.TypeId == 32375).Single();
            key.LocationId = ship;
            key.FlagId = (int)InventoryFlag.Cargo;
            world.Commit();
        }
        Assert.Empty(missions.ActivateGate(Pilot, gate.Id, gate.Destination).Items);
        Recover(f, gas, GasContainer);
        Dock(f, station);
        var completed = missions.Act(Pilot, Agent, MissionAction.Complete, gas.Id);
        Assert.Equal(MissionState.Completed, completed.Offer!.State);
        Assert.Contains(completed.Items, item => item.Item.TypeId == 591);
        Assert.Contains(Agent, missions.CompletedCareerAgents(Pilot, new HashSet<long> { Agent }));
        Assert.Empty(missions.TrainingSites(Pilot, gas.SystemId));
        Assert.Empty(
            live.Economy.Inspect(world =>
                world
                    .Set<SavedHackingContainer>()
                    .AsEnumerable()
                    .Where(container => MissionContainerRules.IsContainer(container.Id))
                    .ToArray()
            )
        );
        await live.FlushAsync();
        f.StopResident();
        using var reopened = Eden.Server.ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<IPilotMissions>();
        Assert.Contains(Agent, restored.CompletedCareerAgents(Pilot, new HashSet<long> { Agent }));
        Assert.Throws<InvalidDataException>(() => restored.Act(Pilot, Agent, MissionAction.Request));
    }

    private static MissionOffer Next(MissionFixture f, MissionOffer previous, int station, int expected)
    {
        Dock(f, station);
        var result = f.Missions.Act(Pilot, Agent, MissionAction.Complete, previous.Id);
        var offer = Assert.IsType<MissionOffer>(result.Continuation);
        Assert.Equal(expected, offer.MissionId);
        return f.Accept(offer);
    }

    private static void HackProof(MissionFixture f, MissionOffer offer, int type, ExplorationKind kind)
    {
        var live = LiveWorld.Find(f.Db.Path)!;
        var site = Assert.Single(f.Missions.TrainingSites(Pilot, offer.SystemId));
        Assert.True(site.IsSignature);
        Assert.Equal(kind, site.Exploration);
        Assert.False(f.Services.GetRequiredService<IPilotProbeScanning>().Resolved(Pilot, site.Id, offer.SystemId, f.Clock.Now));
        var target = Assert.Single(site.Objects, body => body.TypeId == type);
        Resolve(f, site);
        Move(f, offer.SystemId, target.Position);
        Assert.Throws<InvalidDataException>(() => f.Services.GetRequiredService<IPilotMissionContainers>().Read(Pilot, target.Id));
        long module;
        using (var action = live.Economy.Begin(new(pilots: [Pilot])))
        {
            module = action.NextItemId();
            action.Add(
                new OwnedItem
                {
                    Id = module,
                    OwnerId = Pilot,
                    LocationId = action.Pilots.Find(Pilot)!.ActiveShipId,
                    FlagId = (int)InventoryFlag.MediumSlot0,
                    TypeId = kind == ExplorationKind.Data ? DataAnalyzer : RelicAnalyzer,
                    Quantity = 1,
                    Singleton = true,
                    Online = true,
                }
            );
            action.Commit();
        }
        var hacking = f.Services.GetRequiredService<IPilotHacking>();
        var contact = hacking.Begin(Pilot, module, target.Id, new HashSet<long> { target.Id }, f.Clock.Now);
        Assert.Equal(kind, contact.Kind);
        var board = contact.Board;
        while (board.Outcome == HackingOutcome.Running)
        {
            var core = board.Tiles.Single(tile => tile.Type == HackingNode.Core);
            var tile = core.Hidden
                ? board.Tiles.First(tile => tile.Hidden && !tile.Blocked && HackingRules.Accessible(board.Tiles, tile))
                : core;
            board = hacking.Act(Pilot, new(HackingActionKind.Click, tile.Coordinate), f.Clock.Now).Current;
        }
        Assert.Equal(HackingOutcome.Won, board.Outcome);
        Recover(f, offer, type);
        using var remove = live.Economy.Begin(new(pilots: [Pilot]));
        remove.Remove(remove.Set<OwnedItem>().Find(module)!);
        remove.Commit();
    }

    private static void Resolve(MissionFixture f, CombatAnomaly site)
    {
        var live = LiveWorld.Find(f.Db.Path)!;
        long ship = Move(f, site.SystemId, site.Position);
        var scanning = f.Services.GetRequiredService<IPilotProbeScanning>();
        var probes = scanning.List(Pilot, site.SystemId, f.Clock.Now);
        if (probes.Count == 0)
        {
            long launcher;
            using (var action = live.Economy.Begin(new(pilots: [Pilot])))
            {
                foreach (var (skill, points) in new[] { (ProbeScanRules.AstrometricsSkill, 750), (ProbeScanRules.ScienceSkill, 8_000) })
                {
                    var row =
                        action.Set<PilotSkill>().Find(Pilot, skill)
                        ?? action.Add(new PilotSkill { PilotId = Pilot, TypeId = skill }).Entity;
                    row.Points = Math.Max(row.Points, points);
                }
                launcher = action.NextItemId();
                action.Add(
                    new OwnedItem
                    {
                        Id = launcher,
                        OwnerId = Pilot,
                        LocationId = ship,
                        FlagId = (int)InventoryFlag.HighSlot0,
                        TypeId = 17938,
                        Quantity = 1,
                        Singleton = true,
                        Online = true,
                    }
                );
                action.Add(
                    new OwnedItem
                    {
                        Id = action.NextItemId(),
                        OwnerId = Pilot,
                        LocationId = launcher,
                        FlagId = (int)InventoryFlag.HighSlot0,
                        TypeId = 30013,
                        Quantity = 8,
                    }
                );
                action.Commit();
            }
            probes = scanning.Launch(Pilot, launcher, 8, f.Clock.Now).Probes;
        }
        var placements = ProbeScanningRuleTests
            .Formation(site.Position)
            .Take(probes.Count)
            .Select((pair, index) => pair.Placement with { Id = probes[index].Id })
            .ToArray();
        var scan = scanning.Begin(Pilot, placements, f.Clock.Now);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(scan.ReadyAt - f.Clock.Now));
        var result = Assert.Single(scanning.Complete(Pilot, scan, f.Clock.Now), row => row.Site.Id == site.Id);
        Assert.Equal(1, result.Certainty);
        Assert.True(scanning.Resolved(Pilot, site.Id, site.SystemId, f.Clock.Now));
    }

    private static void Recover(MissionFixture f, MissionOffer offer, int type)
    {
        var target = f
            .Missions.Sites(offer.SystemId)
            .Where(site => site.Id == offer.Id)
            .SelectMany(site => site.Objects)
            .Single(body => body.TypeId == type);
        long ship = Move(f, offer.SystemId, target.Position);
        var containers = f.Services.GetRequiredService<IPilotMissionContainers>();
        var cargo = Assert.Single(containers.Read(Pilot, target.Id).Items);
        var changes = containers.Take(Pilot, ship, new([cargo.Id], target.Id, (int)InventoryFlag.Cargo, cargo.Quantity));
        Assert.Contains(changes, change => change.Item.TypeId == offer.Retrieval!.TypeId);
        Assert.True(f.Missions.Current(Pilot, Agent)!.ObjectiveComplete);
    }

    private static long Move(MissionFixture f, int system, SpacePosition at)
    {
        using var action = LiveWorld.Find(f.Db.Path)!.Economy.Begin(new(pilots: [Pilot]));
        long ship = action.Pilots.Find(Pilot)!.ActiveShipId;
        var row = action.Positions.Find(ship) ?? action.Add(new FlightPosition { ShipId = ship }).Entity;
        (row.SystemId, row.X, row.Y, row.Z) = (system, at.X, at.Y, at.Z);
        action.Commit();
        return ship;
    }

    private static void Dock(MissionFixture f, int station)
    {
        using var action = LiveWorld.Find(f.Db.Path)!.Economy.Begin(new(pilots: [Pilot]));
        var ship = action.Pilots.Find(Pilot)!.ActiveShip;
        if (ship.Position is { } position)
        {
            action.Remove(position);
        }
        ship.StationId = station;
        action.Commit();
    }
}
