using Eden.Data.Catalog.Capsuleers;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.Combat;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class PilotSpaceActivitiesTests
{
    private const int Station = 60015157;
    private const int SystemId = 30000141;

    internal static TrafficProfile CombatProfile =>
        TrafficSnapshotTests.Profile() with
        {
            HullTypeId = 603,
            Equipment = [new(561, (int)InventoryFlag.HighSlot0, 1), new(215, (int)InventoryFlag.HighSlot0, 50)],
        };

    internal static long FitTraveler(CapsuleerHaulerTests.Fixture f)
    {
        TrafficSnapshotTests.Install(f.Database, CombatProfile);
        long pilotId = f.Founder().PilotId;
        using WorldDbContext world = new(f.Database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(f.Database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
        TrafficFitting.Apply(world, catalog, pilotId, pilot.ActiveShipId, CombatProfile, f.Clock.Now);
        world.Add(
            new SavedTrafficIdentity
            {
                PilotId = pilotId,
                CharacterId = CombatProfile.CharacterId,
                KillId = CombatProfile.KillId,
                SnapshotId = "test",
            }
        );
        world.SaveChanges();
        transaction.Commit();
        return pilotId;
    }

    [Fact]
    public void ResupplyPaysTheSellerAndPersistsOneQuarterBudgetAcrossRetries()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        long buyer = FitTraveler(f);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: false);
        ResupplyProgress progress;
        long seller;
        long sellerBefore;
        using (WorldDbContext world = new(f.Database.Path))
        {
            using var transaction = world.Database.BeginTransaction();
            using CatalogDbContext catalog = new(f.Database.CatalogPath);
            seller = world.Set<SavedCapsuleerAgent>().Single(a => a.Career == CapsuleerCareer.Mining).PilotId;
            world.Set<PilotWallet>().Find(buyer)!.Cents = 10000;
            var item = new OwnedItem
            {
                Id = InventoryPersistence.NextId(world),
                OwnerId = seller,
                LocationId = Station,
                TypeId = 215,
                Quantity = 1000,
                FlagId = (int)InventoryFlag.Hangar,
            };
            world.Add(item);
            world.SaveChanges();
            host.GetRequiredService<PilotMarketStore>().ExecuteSell(world, catalog, seller, [new(item.Id, Station, 215, 100, 1000)], 30);
            sellerBefore = world.Set<PilotWallet>().Find(seller)!.Cents;
            progress = host.GetRequiredService<CapsuleerResupply>().Dock(f.Database, world, catalog, buyer, null, f.Clock.Now);
            Assert.Equal(2500, progress.BudgetCents);
            Assert.Equal(2500, progress.SpentCents);
            Assert.Equal(7500, world.Set<PilotWallet>().Find(buyer)!.Cents);
            Assert.True(world.Set<PilotWallet>().Find(seller)!.Cents > sellerBefore);
            Assert.Equal(
                25,
                world
                    .Set<OwnedItem>()
                    .Where(i =>
                        i.OwnerId == buyer
                        && i.LocationId == world.Pilots.Find(buyer)!.ActiveShipId
                        && i.FlagId == (int)InventoryFlag.Cargo
                        && i.TypeId == 215
                    )
                    .Sum(i => i.Quantity)
            );
            transaction.Commit();
        }
        WorldInitialization.Initialize(f.Database, f.Clock);
        using WorldDbContext reopened = new(f.Database.Path);
        using var retry = reopened.Database.BeginTransaction();
        using CatalogDbContext source = new(f.Database.CatalogPath);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ResupplyProgress>(System.Text.Json.JsonSerializer.Serialize(progress));
        var result = host.GetRequiredService<CapsuleerResupply>().Dock(f.Database, reopened, source, buyer, restored, f.Clock.Now);
        Assert.Equal(progress.SpentCents, result.SpentCents);
        Assert.Equal(7500, reopened.Set<PilotWallet>().Find(buyer)!.Cents);
        Assert.Equal(975, reopened.Set<SavedPersonalOrder>().Single(o => o.PilotId == seller).Remaining);
    }

    [Fact]
    public void FrozenRattingPreservesPaidDeadlinesEvenWhenTheHumanClockWasStopped()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        long pilot = FitTraveler(f);
        long started = f.Clock.Now;
        long ready = started + TimeSpan.FromSeconds(10).Ticks;
        using (WorldDbContext world = new(f.Database.Path))
        {
            long ship = world.Pilots.Find(pilot)!.ActiveShipId;
            var progress = new HaulerProgress(HaulerActivity.Flying, 0, 0, ship, Station, 0, new NavigationState().Capture())
            {
                Ratting = new(Station, [40009051], 0, TimeSpan.FromMinutes(10).Ticks)
                {
                    Phase = RattingPhase.Fighting,
                    LastObservedAt = started,
                },
                Defense = new CapsuleerDefenseState
                {
                    ShipId = ship,
                    Locks = [new(42, ready)],
                    Cycles = [new(123, 561, DogmaEffect.ProjectileFired, 42, started, ready)],
                },
            };
            new HaulerCheckpoint(pilot, progress).Apply(world);
        }
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: false);
        var job = host.GetRequiredService<ICapsuleerScheduler>().Jobs().Single(j => j.PilotId == pilot);
        var hauler = host.GetRequiredService<CapsuleerHaulerFactory>().Create(pilot);
        f.Clock.Seconds += 300;
        hauler.Resume(job, 0);
        var work = hauler.Read()!.Work;
        Assert.Equal(ready + TimeSpan.FromSeconds(300).Ticks, work.Defense!.Locks.Single().ReadyAt);
        Assert.Equal(ready + TimeSpan.FromSeconds(300).Ticks, work.Defense.Cycles.Single().ReadyAt);
        Assert.Equal(TimeSpan.FromMinutes(10).Ticks, work.Ratting!.RemainingTicks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DockedResupplyReloadsAnEmptyMagazineFromOwnedStockWithoutGrantingAnything(bool ready)
    {
        using CapsuleerHaulerTests.Fixture f = new();
        long pilot = FitTraveler(f);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: false);
        using WorldDbContext world = new(f.Database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(f.Database.CatalogPath);
        long ship = world.Pilots.Find(pilot)!.ActiveShipId;
        long gun = world.Set<OwnedItem>().Single(i => i.LocationId == ship && i.FlagId == (int)InventoryFlag.HighSlot0).Id;
        world.Remove(world.Set<OwnedItem>().Single(i => i.LocationId == gun));
        world.Set<PilotWallet>().Find(pilot)!.Cents = 0;
        world.Add(
            new OwnedItem
            {
                Id = InventoryPersistence.NextId(world),
                OwnerId = pilot,
                LocationId = Station,
                TypeId = 215,
                Quantity = 100,
                FlagId = (int)InventoryFlag.Hangar,
            }
        );
        world.SaveChanges();
        if (ready)
        {
            f.Clock.Seconds += HaulingRules.PollSeconds;
        }
        var result = host.GetRequiredService<CapsuleerResupply>().Dock(f.Database, world, catalog, pilot, null, f.Clock.Now);
        Assert.Equal(0, result.SpentCents);
        Assert.Equal(0, world.Set<PilotWallet>().Find(pilot)!.Cents);
        Assert.Equal(ready, world.Set<OwnedItem>().Any(i => i.LocationId == gun && i.Quantity > 0));
        Assert.Equal(100, world.Set<OwnedItem>().Where(i => i.OwnerId == pilot && i.TypeId == 215).Sum(i => i.Quantity));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ShoppingChoosesRealAffordableStockAndHonorsThreatMemory(bool overpriced, bool avoided)
    {
        using CapsuleerHaulerTests.Fixture f = new();
        long buyer = FitTraveler(f);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: false);
        using WorldDbContext world = new(f.Database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(f.Database.CatalogPath);
        long seller = world.Set<SavedCapsuleerAgent>().Single(a => a.Career == CapsuleerCareer.Mining).PilotId;
        world.Ships.Find(world.Pilots.Find(seller)!.ActiveShipId)!.StationId = 60003760;
        var item = new OwnedItem
        {
            Id = InventoryPersistence.NextId(world),
            OwnerId = seller,
            LocationId = 60003760,
            TypeId = 215,
            Quantity = 1000,
            FlagId = (int)InventoryFlag.Hangar,
        };
        world.Add(item);
        world.SaveChanges();
        long price = CapsuleerResupply.Limit(215) + (overpriced ? 1 : 0);
        host.GetRequiredService<PilotMarketStore>().ExecuteSell(world, catalog, seller, [new(item.Id, 60003760, 215, price, 1000)], 30);
        ResupplyProgress progress = new(Station, 2500000, 0) { Requirements = [new(215, 250, false, .025)] };
        var route = host.GetRequiredService<CapsuleerResupply>()
            .Shop(
                world,
                catalog,
                buyer,
                Station,
                Eden.Gameplay.Ships.HullClass.Frigate,
                progress,
                avoided ? new HashSet<int> { 30000142 } : new HashSet<int>()
            );
        Assert.Equal(!overpriced && !avoided, route is not null);
        if (route is not null)
        {
            Assert.Equal(60003760, route.DestinationStationId);
            Assert.Equal(new[] { 30000141, 30000142 }, route.Systems);
        }
    }

    [Fact]
    public void ProactivePirateTargetDoesNotInventAnIncomingAttackOrRepeatPaidCycles()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
        var controller = f.Db.Services.GetRequiredService<CapsuleerDefense>();
        var enemy = f.Npc;
        CapsuleerAttacker target = new(0, enemy.Id, enemy.TypeId, enemy.Position, SpacePosition.Zero, enemy.Radius, 33, 0, false, true);
        NavigationState navigation = new();
        var before = enemy.Health.Remaining;
        var locking = controller.Advance(f.Pilot, navigation, null, f.Clock.Now, 0, [], proactive: target);
        Assert.False(locking.NewThreat);
        Assert.True(locking.HoldsNavigation);
        f.Clock.Milliseconds += (locking.State!.Locks.Single().ReadyAt - f.Clock.Now) / TimeSpan.TicksPerMillisecond + 1;
        var fired = controller.Advance(locking.Pilot, navigation, locking.State, f.Clock.Now, 10, [], proactive: target);
        Assert.Contains(fired.State!.Cycles, c => c.ModuleId == f.Module);
        var after = f
            .Db.Services.GetRequiredService<INpcEncounters>()
            .List(enemy.SystemId, f.Clock.Now)
            .Single(n => n.Id == enemy.Id)
            .Health.Remaining;
        Assert.True(after.Shield < before.Shield);
        controller.Advance(fired.Pilot, navigation, fired.State, f.Clock.Now, 10, [], proactive: target);
        Assert.Equal(
            after,
            f.Db.Services.GetRequiredService<INpcEncounters>()
                .List(enemy.SystemId, f.Clock.Now)
                .Single(n => n.Id == enemy.Id)
                .Health.Remaining
        );
    }

    [Fact]
    public void ActualCombatFitCanClearAnOrdinaryPirateAndCannotCollectItsBountyTwice()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
        PassiveCatalogFixture.LoadStarterProduction(new WorldDatabase(f.Db.Path).CatalogPath);
        using (WorldDbContext world = new(f.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath))
        {
            long shipId = NpcCombatTests.Fixture.ShipId;
            world.Positions.Remove(world.Positions.Single(position => position.ShipId == shipId));
            world.Remove(world.Set<OwnedItem>().Find(f.Module)!);
            world.SaveChanges();
            TrafficFitting.Apply(world, catalog, NpcCombatTests.Fixture.PilotId, shipId, CombatProfile, f.Clock.Now);
            Eden.World.Docking.SqliteWorld.SetPosition(
                world,
                catalog,
                shipId,
                f.Npc.Position + new SpacePosition(5000, 0, 0),
                f.Npc.SystemId,
                f.Clock.Now
            );
            world.SaveChanges();
            transaction.Commit();
        }
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            int bountyAttribute = (int)Eden.Gameplay.Dogma.DogmaAttributeId.EntityKillBounty;
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO DogmaAttribute VALUES ({bountyAttribute},'entityKillBounty')");
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES ({f.Npc.TypeId},{bountyAttribute},1000)");
        }
        var activity = f.Db.Services.GetRequiredService<CapsuleerRatting>();
        var controller = f.Db.Services.GetRequiredService<CapsuleerDefense>();
        var enemy = f.Db.Services.GetRequiredService<INpcEncounters>().List(f.Npc.SystemId, f.Clock.Now).Single(n => n.Id == f.Npc.Id);
        var assessment = activity.Assess(f.Pilot, [enemy], f.Clock.Now);
        Assert.True(
            assessment.Capable,
            assessment.Reason
                + " Health="
                + f.Health.Read(f.Pilot.PilotId, f.Clock.Now)
                + " Weapons="
                + System.Text.Json.JsonSerializer.Serialize(
                    f.Db.Services.GetRequiredService<CapsuleerDefense>().Profile(f.Pilot.PilotId).RattingWeapons
                )
        );
        CapsuleerDefenseState? defense = null;
        var pilot = f.Pilot;
        NavigationState navigation = new();
        bool destroyed = false;
        for (int i = 0; i < 180; i++)
        {
            f.Clock.Milliseconds += 1000;
            CapsuleerAttacker target = new(0, enemy.Id, enemy.TypeId, enemy.Position, SpacePosition.Zero, enemy.Radius, 33, 0, false, true);
            var turn = controller.Advance(pilot, navigation, defense, f.Clock.Now, i, [], proactive: target);
            pilot = turn.Pilot;
            defense = turn.State;
            destroyed = f.Db.Services.GetRequiredService<INpcEncounters>().WasDestroyed(enemy.Id);
            if (destroyed)
            {
                break;
            }
        }
        Assert.True(destroyed);
        using WorldDbContext saved = new(f.Db.Path);
        var reward = Assert.Single(saved.Set<NpcBounty>().Where(b => b.NpcId == enemy.Id));
        long balance = saved.Set<PilotWallet>().Find(pilot.PilotId)!.Cents;
        controller.Advance(pilot, navigation, defense, f.Clock.Now, 200, []);
        using WorldDbContext reread = new(f.Db.Path);
        Assert.Equal(reward.Id, Assert.Single(reread.Set<NpcBounty>().Where(b => b.NpcId == enemy.Id)).Id);
        Assert.Equal(balance, reread.Set<PilotWallet>().Find(pilot.PilotId)!.Cents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TravelerVisitsABeltProgressesWhenUnobservedAndReturnsToTheActualStation(bool loot)
    {
        using CapsuleerHaulerTests.Fixture f = new();
        long pilot = FitTraveler(f);
        using (WorldDbContext world = new(f.Database.Path))
        {
            long ship = world.Pilots.Find(pilot)!.ActiveShipId;
            world.Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = pilot,
                    LocationId = ship,
                    TypeId = 215,
                    Quantity = 250,
                    FlagId = (int)InventoryFlag.Cargo,
                }
            );
            long choice = Enumerable.Range(1, 100).First(i => RattingRules.Choose(pilot, i));
            var progress = new HaulerProgress(
                HaulerActivity.Docked,
                0,
                0,
                ship,
                Station,
                0,
                new NavigationState().Capture(),
                Journey: new(TrafficJourneyKind.Local, Station, Station, Station, [SystemId], 1)
            )
            {
                ActivitySequence = choice - 1,
            };
            world.Set<SavedCapsuleerHauler>().Single(h => h.PilotId == pilot).Progress = System.Text.Json.JsonSerializer.Serialize(
                progress,
                new System.Text.Json.JsonSerializerOptions { IgnoreReadOnlyProperties = true }
            );
            world.SaveChanges();
        }
        if (loot)
        {
            using (CatalogDbContext edited = new(f.Database.CatalogPath, writable: true))
            {
                edited.Database.ExecuteSqlRaw(
                    """
                    INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES(186,'Wreck',2);
                    INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Mass,Radius,Capacity) VALUES(26557,'Frigate Wreck',186,10000,14,27500);
                    """
                );
            }
            using CatalogDbContext catalog = new(f.Database.CatalogPath);
            var belt = SystemObjects.Geometry(catalog, SystemId).Single(c => c.Id == 40009051);
            using WorldDbContext world = new(f.Database.Path);
            for (int index = 0; index < 2; index++)
            {
                long owner = index == 0 ? pilot : world.Set<SavedCapsuleerAgent>().Single(a => a.Career == CapsuleerCareer.Mining).PilotId;
                long npc = 4000000000099 + index;
                long wreck = 8000000000099 + index;
                world.Add(
                    new WorldNpc
                    {
                        Id = npc,
                        BeltId = 40009051,
                        SystemId = SystemId,
                        TypeId = 601,
                        DestroyedAt = f.Clock.Now,
                    }
                );
                world.Add(
                    new WorldWreck
                    {
                        Id = wreck,
                        NpcId = npc,
                        OwnerId = owner,
                        TypeId = 26557,
                        Name = "Owned test wreck",
                        SystemId = SystemId,
                        X = belt.Position.X + 1000,
                        Y = belt.Position.Y,
                        Z = belt.Position.Z,
                        CreatedAt = f.Clock.Now,
                        ExpiresAt = f.Clock.Now + TimeSpan.FromHours(2).Ticks,
                    }
                );
                world.Add(
                    new OwnedItem
                    {
                        Id = InventoryPersistence.NextId(world),
                        OwnerId = owner,
                        LocationId = wreck,
                        TypeId = 34,
                        Quantity = 100,
                        FlagId = (int)InventoryFlag.Cargo,
                    }
                );
                world.SaveChanges();
            }
        }
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        Guid observer = Guid.NewGuid();
        var presence = host.GetRequiredService<IHumanPresence>();
        presence.Update(observer, SystemId);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
        HaulerObservation? state = null;
        for (int i = 0; i < 100; i++)
        {
            f.Clock.Seconds++;
            maintenance.Tick();
            state = scheduler.Hauler(pilot)!;
            if (state.Work.Ratting is not null)
            {
                break;
            }
        }
        Assert.NotNull(state!.Work.Ratting);
        Assert.Equal(RattingPhase.TravellingToBelt, state.Work.Ratting.Phase);
        Assert.Equal(40009051L, state.Work.Ratting.Belts.Single());
        presence.Update(observer, 30000142);
        maintenance.Tick();
        long remaining = scheduler.Hauler(pilot)!.Work.Ratting!.RemainingTicks;
        long due = scheduler.Jobs().Single(j => j.PilotId == pilot).DueTicks;
        f.Clock.Milliseconds += Math.Max(
            (long)CapsuleerCoarseRules.Interval.TotalMilliseconds,
            (long)Math.Ceiling((due - presence.Read().ElapsedTicks) / (double)TimeSpan.TicksPerMillisecond)
        );
        for (int turn = 0; turn < CapsuleerCoarseRules.PageSize; turn++)
        {
            maintenance.Tick();
            f.Clock.Milliseconds += 50;
        }
        var remote = scheduler.Hauler(pilot)!;
        Assert.True(remote.Work.Ratting is null || remote.Work.Ratting.RemainingTicks < remaining);
        presence.Update(observer, SystemId);
        state = remote;
        bool completed = state.Work.Ratting is null && state.Pilot.Position is null;
        for (int i = 0; i < 1000 && !completed; i++)
        {
            f.Clock.Seconds++;
            maintenance.Tick();
            state = scheduler.Hauler(pilot)!;
            Assert.Null(state.Work.WaitingReason);
            if (state.Work.Ratting is null && state.Pilot.Position is null)
            {
                completed = true;
                break;
            }
        }
        Assert.True(completed, $"Last phase {state!.Work.Ratting?.Phase}, activity {state.Work.Activity}");
        Assert.Equal(Station, state!.Pilot.StationId);
        if (loot)
        {
            using (WorldDbContext saved = new(f.Database.Path))
            {
                Assert.Equal(
                    100,
                    saved.Set<OwnedItem>().Where(i => i.OwnerId == pilot && i.LocationId == Station && i.TypeId == 34).Sum(i => i.Quantity)
                );
                Assert.Empty(saved.Set<OwnedItem>().Where(i => i.LocationId == 8000000000099));
                Assert.Equal(100, saved.Set<OwnedItem>().Single(i => i.LocationId == 8000000000100).Quantity);
            }
            await host.GetRequiredService<LiveWorld>().FlushAsync();
            host.Dispose();
            WorldInitialization.Initialize(f.Database, f.Clock);
            using WorldDbContext reopened = new(f.Database.Path);
            Assert.Equal(
                100,
                reopened.Set<OwnedItem>().Where(i => i.OwnerId == pilot && i.LocationId == Station && i.TypeId == 34).Sum(i => i.Quantity)
            );
        }
    }
}
