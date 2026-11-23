using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Mining;
using Eden.Data.World.Skills;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Industry;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class CareerIndustryTests
{
    private const int Entrepreneur = 25;
    private const int Producer = 27;
    private const int Caldari = 500001;
    private const int Miner = (int)MiningLaserType.MinerI;
    private const int Veldspar = 1230;
    private const int CivilianSalvager = 26983;
    private const int CivilianTransportWreck = 27275;
    private const int BlackBox = 27276;

    [Fact]
    public void FirstOreHarvestUnlocksProducerTurnInWithoutDepletingThePocket()
    {
        using var f = Create();
        var agent = DockAtAgent(f, Producer);
        MissionOffer mission = f.Accept(f.Request(agent.Id));
        Assert.Equal(3234, mission.MissionId);
        int station = agent.StationId!.Value;
        long module = FitGift(f, station, Miner);
        var live = Live(f);
        using (var world = live.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            world.Add(
                new OwnedItem
                {
                    Id = world.NextItemId(),
                    OwnerId = MissionFixture.PilotId,
                    TypeId = Veldspar,
                    Quantity = 1000,
                    LocationId = station,
                    FlagId = (int)InventoryFlag.Hangar,
                }
            );
            world.Commit();
        }
        Assert.Throws<InvalidDataException>(() => Complete(f, mission));
        var site = Assert.Single(f.Missions.Sites(mission.SystemId));
        var deposit = Assert.Single(site.Objects, o => MissionMiningRules.IsDeposit(o.Id));
        Assert.True(site.Private);
        Move(f, mission, deposit.Position);
        var yield = Mine(f, module, deposit.Id);
        Assert.False(yield.Depleted);
        Assert.True(yield.Extracted > 0);
        Dock(f, station);
        var complete = Complete(f, mission);
        Assert.Equal(MissionState.Completed, complete.Offer!.State);
        Assert.Equal(13873, complete.Continuation!.MissionId);
        Assert.Null(live.Economy.Inspect(world => world.Set<WorldAsteroid>().Find(deposit.Id)));
    }

    [Fact]
    public async Task EntrepreneurMiningAmbushActivatesOnceAndSurvivesCheckpoint()
    {
        using var f = Create();
        var agent = DockAtAgent(f, Entrepreneur, previous: 3181);
        var mission = f.Accept(f.Request(agent.Id));
        Assert.Equal(13848, mission.MissionId);
        var site = Assert.Single(f.Missions.Sites(mission.SystemId));
        var deposit = Assert.Single(site.Objects, o => MissionMiningRules.IsDeposit(o.Id));
        var live = Live(f);
        f.Missions.Arrive(mission.SystemId, site.Position);
        Assert.Empty(Actors(live, mission.Id));
        long module = FitGift(f, agent.StationId!.Value, Miner);
        Move(f, mission, deposit.Position);
        Assert.True(Mine(f, module, deposit.Id).Extracted > 0);
        long hostile = Assert.Single(Actors(live, mission.Id)).Id;
        Assert.True(Mine(f, module, deposit.Id).Extracted > 0);
        Assert.Equal(hostile, Assert.Single(Actors(live, mission.Id)).Id);
        Assert.False(f.Missions.Current(MissionFixture.PilotId, agent.Id)!.ObjectiveComplete);
        await live.FlushAsync();
        f.StopResident();
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        Assert.Equal(hostile, Assert.Single(Actors(reopened.GetRequiredService<LiveWorld>(), mission.Id)).Id);
    }

    [Fact]
    public async Task CivilianSalvagerRecoversOneBlackBoxFromTheGuardedCareerWreck()
    {
        using var f = Create(catalog =>
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=100 WHERE ItemTypeId=27275 AND AttributeId=901")
        );
        var agent = DockAtAgent(f, Entrepreneur, previous: 3203);
        var mission = f.Accept(f.Request(agent.Id));
        Assert.Equal(3181, mission.MissionId);
        var live = Live(f);
        long wreckId = MissionSalvageRules.WreckId(mission.Id);
        var wreck = live.Economy.Inspect(world => world.Set<WorldWreck>().Find(wreckId))!;
        Assert.Equal(CivilianTransportWreck, wreck.TypeId);
        await live.FlushAsync();
        long module = FitGift(f, agent.StationId!.Value, CivilianSalvager);
        var room = f.Missions.Sites(mission.SystemId).OrderBy(site => site.RoomId).Last();
        Assert.DoesNotContain(room.Objects, obj => obj.TypeId == CivilianTransportWreck);
        f.Missions.Arrive(mission.SystemId, room.Position);
        Move(f, mission, new(wreck.X, wreck.Y, wreck.Z));
        var information = f.Services.GetRequiredService<IPilotInformation>();
        var locked = new HashSet<long> { wreckId };
        var error = Assert.Throws<InvalidDataException>(() =>
            information.Begin(MissionFixture.PilotId, module, DogmaEffect.Salvaging, wreckId, locked, f.Clock.Now)
        );
        Assert.Contains("Defeat the guards", error.Message);
        f.Kill(Assert.Single(Actors(live, mission.Id)).Id);
        Assert.False(f.Missions.Current(MissionFixture.PilotId, agent.Id)!.ObjectiveComplete);
        var salvage = information.Begin(MissionFixture.PilotId, module, DogmaEffect.Salvaging, wreckId, locked, f.Clock.Now);
        f.Clock.UtcNow = DateTimeOffset.FromFileTime(salvage.Cycle.ReadyAt);
        var result = information.Complete(MissionFixture.PilotId, module, salvage.Cycle.ReadyAt, f.Clock.Now).Salvage!;
        Assert.True(result.Success);
        Assert.Equal(BlackBox, Assert.Single(result.Cargo).Item.TypeId);
        Assert.Throws<InvalidDataException>(() => information.Complete(MissionFixture.PilotId, module, salvage.Cycle.ReadyAt, f.Clock.Now));
        Dock(f, agent.StationId.Value);
        Assert.Equal(MissionState.Completed, Complete(f, mission).Offer!.State);
        Assert.Null(live.Economy.Inspect(world => world.Set<WorldWreck>().Find(wreckId)));
        await live.FlushAsync();
        f.StopResident();
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        Assert.Null(reopened.GetRequiredService<LiveWorld>().Economy.Inspect(world => world.Set<WorldWreck>().Find(wreckId)));
    }

    [Fact]
    public void AllFourCivilianShuttleCopiesExposeThePinnedManufacturingRecipes()
    {
        using var f = Create();
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var recipes = ManufacturingCatalog.Recipes(catalog);
        foreach ((int blueprint, int hull) in new[] { (27300, 27299), (27302, 27301), (27304, 27303), (27306, 27305) })
        {
            var recipe = recipes[blueprint];
            Assert.Equal(hull, recipe.ProductTypeId);
            Assert.Equal(1, recipe.OutputQuantity);
            Assert.Equal(300, recipe.Seconds);
            Assert.Equal(2778, Assert.Single(recipe.Materials).Quantity);
        }
    }

    private static MissionFixture Create(Action<CatalogDbContext>? configure = null) =>
        new(
            live: true,
            configure: catalog =>
            {
                PassiveCatalogFixture.LoadCareerMissions(catalog.Database.GetDbConnection().DataSource);
                configure?.Invoke(catalog);
            }
        );

    private static CatalogAgent DockAtAgent(MissionFixture f, int division, int previous = 0)
    {
        var agent = f
            .Services.GetRequiredService<INpcDirectory>()
            .Agents()
            .First(a => a is { Type: AgentType.Career, FactionId: Caldari } && a.DivisionId == division);
        Dock(f, agent.StationId!.Value);
        if (previous > 0)
        {
            using var world = Live(f).Economy.Begin(new(pilots: [MissionFixture.PilotId]));
            world.Add(
                new SavedMission
                {
                    PilotId = MissionFixture.PilotId,
                    AgentId = agent.Id,
                    MissionId = previous,
                    Level = 1,
                    State = MissionState.Completed,
                    OfferedAt = f.Clock.Now - 3,
                    AcceptedAt = f.Clock.Now - 2,
                    ClosedAt = f.Clock.Now - 1,
                    ExpiresAt = long.MaxValue,
                    SystemId = agent.SystemId!.Value,
                    DestinationStation = agent.StationId.Value,
                    ObjectiveComplete = true,
                }
            );
            world.Commit();
        }
        return agent;
    }

    private static void Dock(MissionFixture f, int station)
    {
        using var world = Live(f).Economy.Begin(new(pilots: [MissionFixture.PilotId]));
        var ship = world.Pilots.Find(MissionFixture.PilotId)!.ActiveShip;
        if (ship.Position is { } position)
        {
            world.Remove(position);
        }
        ship.StationId = station;
        world.Commit();
    }

    private static long FitGift(MissionFixture f, int station, int type)
    {
        using var world = Live(f).Economy.Begin(new(pilots: [MissionFixture.PilotId]));
        long ship = world.Pilots.Find(MissionFixture.PilotId)!.ActiveShipId;
        int high = (int)InventoryFlag.HighSlot0;
        var old = world.Set<OwnedItem>().FirstOrDefault(i => i.LocationId == ship && i.FlagId == high);
        if (old is not null)
        {
            old.LocationId = station;
            old.FlagId = (int)InventoryFlag.Hangar;
            old.Online = false;
        }
        var item = world.Set<OwnedItem>().Single(i => i.OwnerId == MissionFixture.PilotId && i.LocationId == station && i.TypeId == type);
        item.LocationId = ship;
        item.FlagId = high;
        item.Singleton = true;
        item.Online = true;
        if (type == Miner && world.Set<PilotSkill>().Find(MissionFixture.PilotId, (int)MiningSkill.Mining) is null)
        {
            world.Add(
                new PilotSkill
                {
                    PilotId = MissionFixture.PilotId,
                    TypeId = (int)MiningSkill.Mining,
                    Points = 250,
                }
            );
        }
        world.Commit();
        return item.Id;
    }

    private static void Move(MissionFixture f, MissionOffer mission, SpacePosition position)
    {
        using var world = Live(f).Economy.Begin(new(pilots: [MissionFixture.PilotId]));
        long ship = world.Pilots.Find(MissionFixture.PilotId)!.ActiveShipId;
        var row = world.Positions.Find(ship) ?? world.Add(new FlightPosition { ShipId = ship }).Entity;
        (row.SystemId, row.X, row.Y, row.Z) = (mission.SystemId, position.X, position.Y, position.Z);
        world.Commit();
    }

    private static MiningYield Mine(MissionFixture f, long module, long rock)
    {
        var mining = f.Services.GetRequiredService<IPilotMining>();
        var cycle = mining.Begin(MissionFixture.PilotId, module, rock, f.Clock.Now, DogmaEffect.MiningLaser);
        f.Clock.Milliseconds += (long)Math.Ceiling(cycle.Laser.DurationMilliseconds);
        return mining.Collect(MissionFixture.PilotId, module, rock, new(1, true, new(0.5, 0, 0.5, 0, 0.5)));
    }

    private static WorldNpc[] Actors(LiveWorld live, long missionId) =>
        live.Economy.Inspect(world =>
            world
                .Set<WorldNpc>()
                .Where(npc =>
                    npc.AnchorKind == NpcAnchorKind.Mission
                    && npc.EncounterId == MissionRules.FirstSiteId + missionId
                    && npc.DestroyedAt == null
                )
                .ToArray()
        );

    private static MissionUpdate Complete(MissionFixture f, MissionOffer mission) =>
        f.Missions.Act(MissionFixture.PilotId, mission.Agent.Id, MissionAction.Complete, mission.Id);

    private static LiveWorld Live(MissionFixture f) => LiveWorld.Find(f.Db.Path)!;
}
