using Eden.Data.World.Capsuleers;
using Eden.Data.World.Corporations;
using Eden.Data.World.Economy;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rules = Eden.Gameplay.Capsuleers.CapsuleerAmmunitionSupplyRules;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAmmunitionSupplyTests
{
    [Fact]
    public async Task CourierTakesFundedFactoryWorkAndFillsTheOrderAfterRestart()
    {
        const int quantity = 100;
        const long price = 100_000;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PrepareCatalog(fixture);
        UseLocalOrders(fixture);
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord VALUES
                    ('client/systems','30000141','{{"securityStatus":1.0}}'),
                    ('sde/npcStations','60015157','{{"operationID":14,"reprocessingEfficiency":0.5,"reprocessingStationsTake":0.05}}');
                """
            );
        }
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);

        long courier;
        long ship;
        long bid;
        long batch;
        long deadline;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
            using (var setup = live.Economy.Begin(TransactionParticipants.None))
            {
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingGrants = false;
                var agents = setup.Set<SavedCapsuleerAgent>().OrderBy(agent => agent.PilotId).ToArray();
                courier = agents.First(agent => agent.Career == CapsuleerCareer.Hauling).PilotId;
                ship = setup.Pilots.Find(courier)!.ActiveShipId;
                foreach (var agent in agents.Where(agent => agent.PilotId != courier))
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                }
                int employer = Charter(setup, catalog, courier, "Courier Foundry", "CRFD", fixture.Clock.Now);
                setup.Set<SavedCorporation>().Find(employer)!.BalanceCents = quantity * price;
                setup.Pilots.Find(Customer)!.ActiveShip.StationId = Depot;
                setup.Add(new PilotWallet { PilotId = Customer, Cents = 2 * quantity * price });
                AddStock(setup, courier, Depot, Rules.BlueprintType, 1);
                AddStock(setup, courier, Depot, Tritanium, 27);
                AddStock(setup, courier, Depot, Pyerite, 21);
                setup.Commit();
            }

            var market = host.GetRequiredService<IPersonalMarket>();
            market.PlaceBuy(Customer, new(Depot, Rules.ChargeType, price, quantity, 7));
            bid = Assert.Single(market.PersonalOrders(Customer)).Id;
            using var selected = host.GetRequiredService<IHumanPresence>().Select(Customer, Home);
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            for (
                int turn = 0;
                turn < 60 && live.HaulerCheckpoints.GetValueOrDefault(courier)?.Work.Activity != HaulerActivity.Waiting;
                turn++
            )
            {
                fixture.Clock.Seconds++;
                maintenance.Tick();
            }
            Assert.True(scheduler.Read().Failures == 0, scheduler.Read().LastFailure);
            var waiting = Assert.IsType<HaulerObservation>(scheduler.Hauler(courier));
            Assert.Null(waiting.Pilot.Position);
            Assert.Equal(ship, waiting.Pilot.ShipId);
            (batch, deadline) = live.Economy.Read(world =>
            {
                var job = Assert.Single(world.Filter<IndustryJob>(job => job.PilotId == courier));
                Assert.Equal(IndustryJobStatus.Installed, job.Status);
                Assert.Equal(Rules.ChargeType, job.ProductTypeId);
                Assert.Equal(quantity, job.OutputQuantity);
                return (job.Id, Assert.IsType<long>(job.CapsuleerEndTicks));
            });
            selected.Dispose();
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        var worker = reopened.GetRequiredService<CapsuleerMaintenance>();
        var industry = reopened.GetRequiredService<IPilotIndustry>();
        Assert.Equal(deadline, restored.Economy.Read(world => world.Set<IndustryJob>().Find(batch)!.CapsuleerEndTicks));
        using var online = reopened.GetRequiredService<IHumanPresence>().Select(Customer, Home);
        fixture.Clock.Seconds += Math.Max(0, deadline - restored.CapsuleerActivity.Read().ElapsedTicks) / TimeSpan.TicksPerSecond;
        for (int turn = 0; turn < 120 && industry.Job(courier, batch).Status != IndustryJobStatus.Delivered; turn++)
        {
            fixture.Clock.Seconds++;
            worker.Tick();
        }
        var resumed = reopened.GetRequiredService<CapsuleerScheduler>().Read();
        Assert.True(resumed.Failures == 0, resumed.LastFailure);
        Assert.Equal(IndustryJobStatus.Delivered, industry.Job(courier, batch).Status);
        restored.Economy.Read(world =>
        {
            Assert.Equal(0, world.Set<SavedPersonalOrder>().Find(bid)!.Remaining);
            Assert.Equal(quantity, Stock(world, Customer, Depot, Rules.ChargeType));
            Assert.Single(world.Filter<IndustryJob>(job => job.PilotId == courier));
            return 0;
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MinerRoamsAfterRestartAndReturnsToMiningWhenReserveDrops(bool unprofitableBid, bool unattended)
    {
        const int frigate = (int)TechOneHullType.Merlin;
        const int gun = (int)CombatWeaponType.GatlingRailgun75mm;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PrepareCatalog(fixture);
        UseLocalOrders(fixture);
        if (unattended)
        {
            var configuration = new WorldConfigurationStore(fixture.Database, fixture.Clock);
            var startup = configuration.Read();
            var draft = startup.Active.Draft;
            configuration.Save(
                startup.Generation,
                draft with
                {
                    Settings = draft.Settings with { Capsuleers = draft.Settings.Capsuleers with { RoamPercent = 100 } },
                },
                "Use a deterministic roam chance for the unattended replay"
            );
        }
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true))
        {
            const string security = "{\"securityStatus\":-0.2}";
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000141',{security})");
            catalog.Database.ExecuteSqlRaw(
                "UPDATE SourceRecord SET Data=json_set(Data,'$.security',-0.2) "
                    + "WHERE Dataset='client/solarsystemcontent' AND Key='30000141'"
            );
        }
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);

        long miner;
        long venture;
        long combat;
        long? lowBid = null;
        CapsuleerJob handoffJob;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
            using (var setup = live.Economy.Begin(TransactionParticipants.None))
            {
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
                var agents = setup.Set<SavedCapsuleerAgent>().ToArray();
                miner = agents.Single(agent => agent.Career == CapsuleerCareer.Mining).PilotId;
                setup.Filter<SavedCapsuleerCommunity>(row => row.PilotId == miner).Single().Progress = null;
                var owner = setup.Pilots.Find(miner)!;
                venture = owner.ActiveShipId;
                foreach (var agent in agents.Where(agent => agent.PilotId != miner))
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                }
                int corporation = Charter(setup, catalog, miner, "Reserve Works", "RESW", fixture.Clock.Now);
                var spare = setup
                    .Add(
                        new Ship
                        {
                            Id = setup.NextItemId(),
                            OwnerId = miner,
                            TypeId = frigate,
                            Name = "Roaming frigate",
                            StationId = Depot,
                        }
                    )
                    .Entity;
                combat = spare.Id;
                CapsuleerCommunityStore.GrantRequirements(setup, catalog, miner, frigate, []);
                PilotFleetStore.Board(setup, catalog, miner, combat, venture, fixture.Clock.Now);
                TrafficFitting.Apply(
                    setup,
                    catalog,
                    miner,
                    combat,
                    TrafficSnapshotTests.Profile() with
                    {
                        HullTypeId = frigate,
                        Equipment = [new(gun, (int)InventoryFlag.HighSlot0, 1), new(Rules.ChargeType, (int)InventoryFlag.HighSlot0, 50)],
                    },
                    fixture.Clock.Now
                );
                PilotFleetStore.Board(setup, catalog, miner, venture, combat, fixture.Clock.Now);
                AddStock(setup, miner, Depot, frigate, 2);
                AddStock(setup, miner, Depot, gun, 1);
                // Keep two complete spare magazines in the hangar after normal launch resupply.
                AddStock(setup, miner, Depot, Rules.ChargeType, 2_000);
                if (unprofitableBid)
                {
                    setup.Pilots.Find(Customer)!.ActiveShip.StationId = Depot;
                    setup.Add(new PilotWallet { PilotId = Customer, Cents = 100_000_000 });
                    AddStock(setup, miner, Depot, Rules.BlueprintType, 1);
                    AddStock(setup, miner, Depot, Tritanium, 27);
                    AddStock(setup, miner, Depot, Pyerite, 21);
                }
                else
                {
                    setup.Add(
                        new OwnedItem
                        {
                            Id = setup.NextItemId(),
                            OwnerId = corporation,
                            TypeId = Rules.ChargeType,
                            LocationId = Depot,
                            Quantity = 400,
                            FlagId = CorporationStorageRules.MarketDeliveriesFlag,
                        }
                    );
                }
                var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(setup, CapsuleerCommunityStore.MinerAccountId, miner)!;
                Assert.Equal(1, CapsuleerIndustry.ReadyHuntReplacements(setup, catalog, pilot, spare));
                AddStock(setup, miner, Depot, gun, 1);
                Assert.Equal(2, CapsuleerIndustry.ReadyHuntReplacements(setup, catalog, pilot, spare));
                Assert.Equal(spare.Id, CapsuleerWorkHulls.Find(setup, catalog, pilot, CapsuleerProfession.Combat, fixture.Clock.Now)?.Id);
                setup.Commit();
            }
            if (unprofitableBid)
            {
                var market = host.GetRequiredService<IPersonalMarket>();
                market.PlaceBuy(Customer, new(Depot, Rules.ChargeType, 1, 100, 7));
                lowBid = Assert.Single(market.PersonalOrders(Customer)).Id;
                using var evaluation = live.Economy.Begin(TransactionParticipants.None);
                using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
                var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(evaluation, CapsuleerCommunityStore.MinerAccountId, miner)!;
                Assert.True(CapsuleerIndustry.HasWorkDemand(evaluation, catalog, pilot, fixture.Clock.Now, requireProductBlueprint: true));
            }
            using var selected = StartActivity(host);
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            for (int turn = 0; turn < 60 && !live.HaulerCheckpoints.ContainsKey(miner); turn++)
            {
                fixture.Clock.Seconds++;
                worker.Tick();
            }
            Assert.True(scheduler.Read().Failures == 0, scheduler.Read().LastFailure);
            Assert.Null(scheduler.Miner(miner));
            var handedOff = Assert.IsType<HaulerObservation>(scheduler.Hauler(miner));
            Assert.Equal(combat, handedOff.Pilot.ShipId);
            Assert.Null(handedOff.Pilot.Position);
            handoffJob = Assert.Single(scheduler.Jobs(), job => job.PilotId == miner);
            Assert.Equal((CapsuleerAction.Trade, handedOff.Work.DueTicks), (handoffJob.Action, handoffJob.DueTicks));
            Assert.Equal(
                CapsuleerCareer.Hauling,
                live.Economy.Read(world => CapsuleerCorporations.Organization(world, miner).ActiveWorker)
            );
            selected?.Dispose();
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        var schedulerAfter = reopened.GetRequiredService<CapsuleerScheduler>();
        var maintenance = reopened.GetRequiredService<CapsuleerMaintenance>();
        var savedJob = restored.Economy.Read(world => world.Set<SavedCapsuleerJob>().Find(miner)!);
        Assert.Equal(
            (handoffJob.Sequence, handoffJob.Action, handoffJob.DueTicks),
            (savedJob.Sequence, savedJob.Action, savedJob.DueTicks)
        );
        using var online = StartActivity(reopened);
        for (int turn = 0; turn < 120 && restored.HaulerCheckpoints.GetValueOrDefault(miner)?.Work.Ratting?.Patrol is null; turn++)
        {
            fixture.Clock.Seconds++;
            maintenance.Tick();
        }
        Assert.True(schedulerAfter.Read().Failures == 0, schedulerAfter.Read().LastFailure);
        Assert.Null(schedulerAfter.Miner(miner));
        var patrol = Assert.IsType<HaulerObservation>(schedulerAfter.Hauler(miner));
        Assert.Equal(combat, patrol.Pilot.ShipId);
        Assert.NotNull(patrol.Pilot.Position);
        Assert.Equal(PatrolKind.Roam, patrol.Work.Ratting?.Patrol?.Kind);
        using (var stock = restored.Economy.Begin(TransactionParticipants.None))
        {
            if (lowBid is long order)
            {
                Assert.Equal(100, stock.Set<SavedPersonalOrder>().Find(order)!.Remaining);
                Assert.Empty(stock.Filter<IndustryJob>(job => job.PilotId == miner));
                Assert.Equal(27, Stock(stock, miner, Depot, Tritanium));
                Assert.Equal(21, Stock(stock, miner, Depot, Pyerite));
            }
            var docked = Assert.IsType<Ship>(stock.Ships.Find(venture));
            Assert.Equal(miner, docked.OwnerId);
            Assert.Equal(Depot, docked.StationId);
            Assert.Null(docked.Position);
            Assert.Contains(stock.FittedItems(miner, venture), item => item.Online && InventoryDefinitions.IsHighSlot(item.FlagId));
            var spent = stock
                .Filter<OwnedItem>(item => item.OwnerId == miner)
                .Where(item => item.LocationId == Depot && item.TypeId == gun && item.FlagId == (int)InventoryFlag.Hangar)
                .OrderBy(item => item.Id)
                .First();
            spent.OwnerId = Customer;
            stock.Commit();
        }
        for (int turn = 0; turn < 1600 && restored.MinerCheckpoints.GetValueOrDefault(miner)?.Work.Activity != MinerActivity.Mining; turn++)
        {
            fixture.Clock.Seconds++;
            maintenance.Tick();
        }
        Assert.True(schedulerAfter.Read().Failures == 0, schedulerAfter.Read().LastFailure);
        Assert.Null(schedulerAfter.Hauler(miner));
        var resumed = Assert.IsType<MinerObservation>(schedulerAfter.Miner(miner));
        Assert.Equal(venture, resumed.Pilot.ShipId);
        Assert.Equal(MinerActivity.Mining, resumed.Work.Activity);
        Assert.Single(schedulerAfter.Jobs(), job => job.PilotId == miner);
        restored.Economy.Read(world =>
        {
            var docked = Assert.IsType<Ship>(world.Ships.Find(combat));
            Assert.Equal(miner, docked.OwnerId);
            Assert.Equal(Depot, docked.StationId);
            Assert.Null(docked.Position);
            Assert.Equal(CapsuleerCareer.Mining, CapsuleerCorporations.Organization(world, miner).ActiveWorker);
            return 0;
        });

        IDisposable? StartActivity(ServiceProvider host)
        {
            var presence = host.GetRequiredService<IHumanPresence>();
            if (!unattended)
            {
                return presence.Select(Customer, Home);
            }
            host.GetRequiredService<CapsuleerActivitySource>().StartUnattended();
            var activity = presence.Read();
            Assert.True(activity.Active);
            Assert.Empty(activity.ActiveSystems);
            return null;
        }
    }
}
