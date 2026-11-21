using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Corporations;
using Eden.Data.World.Economy;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Reprocessing;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.Server.Launcher;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Docking;
using Eden.World.Industry;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Industry;

public sealed partial class FreshUniverseTests(ITestOutputHelper output)
{
    // The pinned catalog's starter system and a station with reprocessing, manufacturing and a market.
    private const int StarterSystem = 30015042;
    private const int Factory = 60015017;
    private const int Miner = (int)MiningLaserType.MinerI;
    private const int Venture = (int)TechOneHullType.Venture;
    private const string CatalogEnvironmentVariable = "EDEN_FRESH_UNIVERSE_CATALOG";

    private sealed class InstalledCatalogFactAttribute : FactAttribute
    {
        public InstalledCatalogFactAttribute()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable(CatalogEnvironmentVariable)))
            {
                Skip = $"Set {CatalogEnvironmentVariable} to the pinned build 3662168 catalog.";
            }
        }
    }

    [Fact]
    public void FreshProfileStartsEmptyAndPersistsAcrossRestart()
    {
        var directory = TestDirectory.Create("eden-fresh-profile-");
        try
        {
            WorldDatabase database = new(Path.Combine(directory.FullName, "world.sqlite"));
            TestCatalog.Create(database.OriginalCatalogPath);
            TestClock clock = new();
            WorldInitialization.Initialize(database, clock, new(FreshUniverse: true));
            WorldInitialization.Initialize(database, clock);
            using var host = ServerServices.Build(database, clock, population: new TestNpcPopulationSeeder(), liveWorld: true);
            var live = host.GetRequiredService<LiveWorld>();
            Assert.Equal(0, live.Economy.Inspect(world => world.Pilots.Count()));
            Assert.Equal(0, live.Economy.Inspect(world => world.Set<NpcMarketOrder>().Count()));
            Assert.Equal(0, live.Economy.Inspect(world => world.Set<TestingWalletGrant>().Count()));
            Assert.True(database.Settings.FreshUniverse);
            Assert.True(database.Settings.Economy.ChargeServiceCosts);
            Assert.False(database.Settings.DevelopmentSupply.TestingGrants);
            var configuration = host.GetRequiredService<IWorldConfiguration>();
            var current = configuration.Read();
            var ordinary = current.Active.Draft with { Settings = current.Active.Draft.Settings with { FreshUniverse = false } };
            Assert.Throws<InvalidDataException>(() => configuration.Save(current.Generation, ordinary, "Test"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [InstalledCatalogFact]
    public void NewCorvettesEarnAndTradeTheFirstManufacturedMinerThroughFundedEscrow() =>
        RunFreshWorldInScratchDirectory(proveCareerShip: false);

    [InstalledCatalogFact]
    public void ZeroIskCorvetteEarnsBuildsFitsAndFliesItsVenture() => RunFreshWorldInScratchDirectory(proveCareerShip: true);

    private void RunFreshWorldInScratchDirectory(bool proveCareerShip)
    {
        var directory = TestDirectory.Create("eden-fresh-universe-");
        try
        {
            RunFreshWorld(directory.FullName, proveCareerShip);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private void RunFreshWorld(string directory, bool proveCareerShip)
    {
        WorldDatabase database = new(Path.Combine(directory, "world.sqlite"));
        File.Copy(Environment.GetEnvironmentVariable(CatalogEnvironmentVariable)!, database.OriginalCatalogPath);
        TestClock clock = new(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero));
        WorldInitialization.Initialize(database, clock, new(FreshUniverse: true));
        WorldInitialization.PrepareCapsuleers(database, maximumCommunities: 0);
        long producer;
        long buyer;
        long orderId;
        long price;
        int shipGoal;
        long corvette;
        using (var host = Open())
        {
            var live = host.GetRequiredService<LiveWorld>();
            Assert.Equal(0, live.Economy.Inspect(world => world.Pilots.Count()));
            var presence = host.GetRequiredService<IHumanPresence>();
            presence.Update(Guid.NewGuid(), StarterSystem);
            var provisioner = host.GetRequiredService<CapsuleerProvisioner>();
            var synchronization = host.GetRequiredService<IWorldSynchronization>();
            for (int slot = 0; slot < StarterIndustryRules.CrewSize; slot++)
            {
                var turn = provisioner.PrepareBackgroundSystem(
                    StarterSystem,
                    synchronization,
                    () => presence.Read().ActiveSystems.Contains(StarterSystem),
                    CancellationToken.None
                );
                Assert.NotNull(turn?.CreatedPilotId);
            }
            var founders = live.Economy.Inspect(world => world.Set<SavedCapsuleerAgent>().ToArray());
            Assert.Equal(2, founders.Length);
            producer = founders.Single(row => row.Slot == StarterIndustryRules.ProducerSlot).PilotId;
            buyer = founders.Single(row => row.Slot == StarterIndustryRules.BuyerSlot).PilotId;
            corvette = live.Economy.Inspect(world => world.Pilots.Find(buyer)!.ActiveShipId);
            CompareWithNewCharacters(host, live, founders);
            using (CatalogDbContext catalog = new(database.CatalogPath))
            {
                var mining = CapsuleerCareerTraining.Ships(catalog, CapsuleerProfession.Mining);
                Assert.Contains((int)TechOneHullType.Venture, mining);
                Assert.Contains((int)CapsuleerMiningHull.Covetor, mining);
                Assert.Contains((int)CapsuleerMiningHull.Hulk, mining);
                Assert.Contains((int)FreightHull.Badger, CapsuleerCareerTraining.Ships(catalog, CapsuleerProfession.Hauling));
                Assert.Contains((int)TechOneHullType.Rifter, CapsuleerCareerTraining.Ships(catalog, CapsuleerProfession.Combat));
                Assert.DoesNotContain((int)TechOneHullType.Venture, CapsuleerCareerTraining.Ships(catalog, CapsuleerProfession.Combat));
            }
            var market = host.GetRequiredService<IPilotMarket>();
            var points = host.GetRequiredService<IPilotFreeSkillPoints>();
            Assert.Empty(market.Orders(buyer, Miner));
            Assert.Empty(market.Orders(buyer, Venture));
            Assert.Throws<InvalidDataException>(() =>
                host.GetRequiredService<IPersonalMarket>().PlaceBuy(buyer, new(Factory, Miner, 1000, 1, 90))
            );
            Assert.Empty(market.Transactions(buyer));
            Assert.Equal(0, market.Balance(buyer));
            Assert.Equal(0, points.Balance(buyer));

            Advance(
                host,
                clock,
                () => host.GetRequiredService<IPersonalMarket>().PersonalOrders(buyer).Any(order => order.TypeId == Miner)
            );
            var order = host.GetRequiredService<IPersonalMarket>().PersonalOrders(buyer).Single(order => order.TypeId == Miner);
            orderId = order.Id;
            price = order.PriceCents;
            Assert.Equal(price, order.EscrowCents);
            Assert.DoesNotContain(host.GetRequiredService<IPilotInventory>().List(buyer, Factory), item => item.TypeId == Miner);
            Assert.Contains(market.Transactions(buyer), trade => !trade.IsBuy && trade.CounterpartyId != producer && trade.PriceCents > 0);
            Assert.Contains(Allocations(live, buyer), entry => entry.TypeId == (int)MiningSkill.Mining && entry.Points > 8000);
            shipGoal = Assert
                .IsType<CapsuleerCareerPlan>(host.GetRequiredService<ICapsuleerScheduler>().Miner(buyer)!.Work.CareerPlan)
                .ShipTypeId;
            Assert.Equal(Venture, shipGoal);
            output.WriteLine($"First funded order after {TimeSpan.FromSeconds(clock.Seconds)}, escrow {price} cents.");
        }

        WorldInitialization.Initialize(database, clock);
        using (var host = Open())
        {
            var savedWork = host.GetRequiredService<LiveWorld>()
                .Economy.Inspect(world => world.Filter<SavedCapsuleerCommunity>(row => row.PilotId == buyer).Single().Progress);
            Assert.Equal(shipGoal, MinerCheckpoint.Read(savedWork)?.CareerPlan?.ShipTypeId);
            var personal = host.GetRequiredService<IPersonalMarket>();
            Assert.Contains(personal.PersonalOrders(buyer), order => order.Id == orderId && order.EscrowCents == price);
            var market = host.GetRequiredService<IPilotMarket>();
            Advance(
                host,
                clock,
                () => market.Transactions(buyer).Any(trade => trade.OrderId == orderId && trade.CounterpartyId == producer)
            );
            AssertSale(host);
            output.WriteLine($"First manufactured item sold after {TimeSpan.FromSeconds(clock.Seconds)}.");
        }

        WorldInitialization.Initialize(database, clock);
        using (var host = Open())
        {
            AssertSale(host);
            if (proveCareerShip)
            {
                var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
                Advance(
                    host,
                    clock,
                    () => scheduler.Miner(buyer) is { Pilot.ShipTypeId: Venture, Work.Activity: MinerActivity.Mining },
                    maximumTurns: 750_000
                );
                AssertCareerShip(host);
                output.WriteLine($"Built and flew the first Venture after {TimeSpan.FromSeconds(clock.Seconds)}.");
            }
            else
            {
                SpendFreshPirateBountyPoints(host, database, clock);
                BuyAndTrainRefiningAfterFirstSale(host, database, clock, producer);
            }
        }
        if (proveCareerShip)
        {
            WorldInitialization.Initialize(database, clock);
            using var host = Open();
            AssertCareerShip(host);
        }

        ServiceProvider Open() => ServerServices.Build(database, clock, population: new TestNpcPopulationSeeder(), liveWorld: true);

        void AssertCareerShip(ServiceProvider host)
        {
            var live = host.GetRequiredService<LiveWorld>();
            var market = host.GetRequiredService<IPilotMarket>();
            var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(CapsuleerCommunityStore.MinerAccountId, buyer)!;
            Assert.Equal(Venture, pilot.ShipTypeId);
            Assert.NotNull(pilot.Position);
            var inventory = host.GetRequiredService<IPilotInventory>();
            Assert.Contains(
                inventory.List(buyer, pilot.ShipId),
                item => item.TypeId == Miner && item.Online && InventoryDefinitions.IsHighSlot(item.FlagId)
            );
            var job = live.Economy.Inspect(world =>
            {
                Assert.Equal(buyer, world.Ships.Find(corvette)!.OwnerId);
                Assert.Empty(world.Set<TestingWalletGrant>());
                return world.Filter<IndustryJob>(row => row.PilotId == buyer).Single(row => row.ProductTypeId == Venture);
            });
            Assert.Equal(
                (IndustryJobStatus.Delivered, StarterIndustryRules.VentureBlueprint, 1),
                (job.Status, job.BlueprintTypeId, job.OutputQuantity)
            );
            Assert.True(job.CostCents > 0);
            var trades = market.Transactions(buyer);
            var blueprint = Assert.Single(trades, trade => trade.IsBuy && trade.TypeId == StarterIndustryRules.VentureBlueprint);
            Assert.True(blueprint.PriceCents > 0);
            Assert.True(trades.Where(trade => !trade.IsBuy).Sum(trade => trade.PriceCents * trade.Quantity) > blueprint.PriceCents);
            Assert.DoesNotContain(trades, trade => trade.TypeId == Venture);
        }

        void AssertSale(ServiceProvider host)
        {
            var live = host.GetRequiredService<LiveWorld>();
            var market = host.GetRequiredService<IPilotMarket>();
            var inventory = host.GetRequiredService<IPilotInventory>();
            var personal = host.GetRequiredService<IPersonalMarket>();
            var fill = Assert.Single(market.Transactions(buyer), trade => trade.TypeId == Miner && trade.IsBuy);
            Assert.Equal((orderId, producer, price, 1), (fill.OrderId, fill.CounterpartyId, fill.PriceCents, fill.Quantity));
            var sale = Assert.Single(market.Transactions(producer), trade => trade.TypeId == Miner && !trade.IsBuy);
            Assert.Equal((buyer, price, 1), (sale.CounterpartyId, sale.PriceCents, sale.Quantity));
            Assert.True(sale.TaxCents > 0);
            Assert.Empty(personal.PersonalOrders(buyer));
            Assert.Equal(0, personal.Escrow(buyer).IskCents);
            Assert.Equal(1, Assert.Single(inventory.List(buyer, Factory), item => item.TypeId == Miner).Quantity);
            Assert.DoesNotContain(inventory.List(producer, Factory), item => item.TypeId == Miner);
            Assert.DoesNotContain(market.Orders(buyer, Miner), order => !order.Bid);
            var job = live.Economy.Inspect(world =>
                world.Filter<IndustryJob>(row => row.PilotId == producer).Where(row => row.ProductTypeId == Miner).MinBy(row => row.Id)!
            );
            Assert.Equal(
                (IndustryJobStatus.Delivered, StarterIndustryRules.MinerBlueprint, Miner, 1),
                (job.Status, job.BlueprintTypeId, job.ProductTypeId, job.OutputQuantity)
            );
            Assert.True(job.CostCents > 0);
            Assert.Contains(market.Transactions(producer), trade => trade.TypeId == StarterIndustryRules.MinerBlueprint && trade.IsBuy);
            var servicePayments = live.Economy.Inspect(world =>
                world.Filter<SavedServicePayment>(row => row.PilotId == producer).ToArray()
            );
            Assert.Contains(servicePayments, payment => payment.Kind == WalletEntryType.ReprocessingTax && payment.AmountCents < 0);
            Assert.Contains(
                servicePayments,
                payment =>
                    payment.Kind == WalletEntryType.Manufacturing && payment.ContextId == job.Id && payment.AmountCents == -job.CostCents
            );
            var producerTraining = Allocations(live, producer);
            Assert.Contains(producerTraining, entry => entry.TypeId == (int)IndustrySkill.Industry && entry.Points > 250);
            Assert.Contains(producerTraining, entry => CapsuleerCareerTrainingRules.Foundation.Any(skill => skill.TypeId == entry.TypeId));
            Assert.NotEmpty(Allocations(live, buyer));
        }
    }

    [InstalledCatalogFact]
    public void FreshPaidMinerRunsCorporateProduction()
    {
        var directory = TestDirectory.Create("eden-fresh-corporate-production-");
        try
        {
            VerifyFreshCorporateProduction(directory.FullName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private void VerifyFreshCorporateProduction(string directory)
    {
        WorldDatabase database = new(Path.Combine(directory, "world.sqlite"));
        File.Copy(Environment.GetEnvironmentVariable(CatalogEnvironmentVariable)!, database.OriginalCatalogPath);
        TestClock clock = new();
        WorldInitialization.Initialize(database, clock, new(FreshUniverse: true));
        using var host = ServerServices.Build(database, clock, population: new TestNpcPopulationSeeder(), liveWorld: true);
        host.GetRequiredService<IHumanPresence>().Update(Guid.NewGuid(), StarterSystem);
        var prepared = host.GetRequiredService<CapsuleerProvisioner>()
            .PrepareBackgroundSystem(StarterSystem, host.GetRequiredService<IWorldSynchronization>(), () => true, CancellationToken.None);
        long factory = Assert.IsType<long>(prepared?.CreatedPilotId);
        var live = host.GetRequiredService<LiveWorld>();
        long customer = CreateSupplier(host, factory);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var recipe = ManufacturingCatalog.Recipes(catalog)[CapsuleerAmmunitionSupplyRules.BlueprintType];
        using (var setup = live.Economy.Begin(TransactionParticipants.None))
        {
            // Resume from a fitted career ship and completed starter trade before installing any corporate jobs.
            var fitted = setup.Pilots.Find(factory)!.ActiveShip;
            fitted.TypeId = Venture;
            setup.Add(
                new OwnedItem
                {
                    Id = setup.NextItemId(),
                    OwnerId = factory,
                    LocationId = fitted.Id,
                    FlagId = (int)InventoryFlag.HighSlot0,
                    TypeId = Miner,
                    Quantity = 1,
                    Singleton = true,
                    Online = true,
                }
            );
            setup.Add(
                new MarketExecution
                {
                    PilotId = factory,
                    TypeId = Miner,
                    StationId = Factory,
                    CounterpartyId = customer,
                    PriceCents = 100_000,
                    Quantity = 1,
                    Time = clock.Now,
                }
            );
            setup.Set<PilotWallet>().Find(factory)!.Cents = CorporationRules.CreationFeeCents;
            foreach (var skill in recipe.Skills)
            {
                var trained =
                    setup.Set<PilotSkill>().Find(factory, skill.TypeId)
                    ?? setup.Add(new PilotSkill { PilotId = factory, TypeId = skill.TypeId }).Entity;
                double rank = catalog
                    .FittingAttributes(skill.TypeId)
                    .GetValueOrDefault(Eden.Gameplay.Dogma.DogmaAttributeId.SkillTimeConstant);
                trained.Points = Math.Max(trained.Points, TrainedSkill.PointsForLevel(rank, skill.Level));
            }
            if (setup.Set<PilotSkill>().Find(factory, CorporationRules.ManagementSkill) is not { } management)
            {
                management = setup.Add(new PilotSkill { PilotId = factory, TypeId = CorporationRules.ManagementSkill }).Entity;
            }
            management.Points = Math.Max(management.Points, 250);
            setup.Pilots.Find(customer)!.ActiveShip.StationId = Factory;
            var customerWallet = setup.Set<PilotWallet>().Find(customer) ?? setup.Add(new PilotWallet { PilotId = customer }).Entity;
            customerWallet.Cents = 10_000_000;
            setup.Add(
                new OwnedItem
                {
                    Id = setup.NextItemId(),
                    OwnerId = factory,
                    LocationId = Factory,
                    FlagId = (int)InventoryFlag.Hangar,
                    TypeId = recipe.BlueprintTypeId,
                    Quantity = 1,
                    Singleton = true,
                    BlueprintRuns = -1,
                }
            );
            foreach (var (type, quantity) in IndustryMaterials.Calculate(recipe.Materials, 1))
            {
                InventoryPersistence.MergeOutput(setup, catalog, factory, Factory, (int)InventoryFlag.Hangar, type, quantity);
            }
            setup.Commit();
        }

        int corporation = host.GetRequiredService<IPlayerCorporations>()
            .Create(factory, PlayerCorporationTests.Details("Fresh Foundry", "FRFR"))
            .Corporation.Id;
        const long capital = 10_000_000;
        using (var treasury = live.Economy.Begin(TransactionParticipants.None))
        {
            var charter = treasury.Set<SavedCorporation>().Find(corporation)!;
            charter.BalanceCents = capital;
            charter.CapsuleerFoundingSystemId = StarterSystem;
            treasury.Commit();
        }
        var market = host.GetRequiredService<IPersonalMarket>();
        market.PlaceBuy(customer, new(Factory, recipe.ProductTypeId, 10_000, recipe.OutputQuantity, 7));
        long order = Assert.Single(market.PersonalOrders(customer)).Id;
        try
        {
            Advance(
                host,
                clock,
                () => live.Economy.Inspect(world => world.Set<SavedPersonalOrder>().Find(order)?.Status == PersonalOrderStatus.Filled),
                maximumTurns: 12_000
            );
        }
        finally
        {
            var diagnostics = live.Economy.Inspect(world =>
            {
                var actor = world.Pilots.Find(factory)!;
                var orderRow = world.Set<SavedPersonalOrder>().Find(order);
                var jobs = world
                    .Filter<IndustryJob>(job => job.PilotId == factory)
                    .AsEnumerable()
                    .Select(job => $"{job.ProductTypeId}/{job.Status}/{job.OutputCorporationId}");
                return $"Hull {actor.ActiveShip.TypeId}, corp {actor.CorporationId}, starter order {orderRow?.Status}. "
                    + $"Jobs {string.Join(", ", jobs)}.";
            });
            output.WriteLine(diagnostics);
        }
        live.Economy.Inspect(world =>
        {
            var job = Assert.Single(
                world.Filter<IndustryJob>(row => row.PilotId == factory),
                row => row.OutputCorporationId == corporation && row.ProductTypeId == recipe.ProductTypeId
            );
            Assert.Equal(IndustryJobStatus.Delivered, job.Status);
            Assert.True(job.CostCents > 0);
            Assert.True(world.Set<SavedCorporation>().Find(corporation)!.BalanceCents > capital);
            return true;
        });
    }

    private static void SpendFreshPirateBountyPoints(ServiceProvider host, WorldDatabase database, TestClock clock)
    {
        // Open a second native factory cohort in the same universe. This rookie has never mined or received SP.
        const int secondSystem = 30001401;
        var live = host.GetRequiredService<LiveWorld>();
        host.GetRequiredService<CapsuleerProvisioner>().EnsureSystem(secondSystem);
        long id = live.Economy.Inspect(world =>
            world
                .Filter<SavedCapsuleerAgent>(agent => agent.CommunitySystemId == secondSystem)
                .Single(agent => agent.Career == CapsuleerCareer.Mining && agent.Slot == StarterIndustryRules.BuyerSlot)
                .PilotId
        );
        var points = host.GetRequiredService<IPilotFreeSkillPoints>();
        Assert.Equal(0, points.Balance(id));
        Assert.Empty(Allocations(live, id));

        using CatalogDbContext catalog = new(database.CatalogPath);
        long earned;
        using (var action = live.Economy.Begin(new(pilots: [id], systems: [secondSystem])))
        {
            earned = SettlePirateKill(action, catalog, id, secondSystem, NpcEncounterDefinitions.GuristasRookieType, clock.Now);
            action.Commit();
        }
        Assert.Equal(earned, points.Balance(id));
        using (var action = live.Economy.Begin(new(pilots: [id])))
        {
            var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(action, CapsuleerCommunityStore.MinerAccountId, id)!;
            var plan = new CapsuleerCareerPlan(CapsuleerProfession.Mining, (int)CapsuleerMiningHull.Hulk, CapsuleerTrainingFocus.Ship);
            var advanced = host.GetRequiredService<CapsuleerCareerTraining>()
                .Advance(action, catalog, pilot, plan.Profession, plan, clock.Now);
            Assert.False(advanced.MarketChanged);
            Assert.Equal(Venture, advanced.Plan!.ShipTypeId);
            Assert.Equal(0, advanced.Plan.HighestHullStage);
            action.Commit();
        }
        Assert.Equal(0, points.Balance(id));
        Assert.Single(Allocations(live, id));
    }

    private static void BuyAndTrainRefiningAfterFirstSale(ServiceProvider host, WorldDatabase database, TestClock clock, long id)
    {
        // Pinned Pith Eliminator payout funds a real skillbook purchase on the existing fresh-world wallet.
        const int pithEliminator = 24145;
        int refining = (int)ReprocessingSkill.Reprocessing;
        var live = host.GetRequiredService<LiveWorld>();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var work = CapsuleerCareerTrainingRules.Work(CapsuleerProfession.Mining, factory: true, management: true);
        Assert.Contains(new(CorporationRules.ManagementSkill, 1), work);
        Assert.Equal(14, CapsuleerCareerTrainingRules.Foundation.Select(target => target.TypeId).Distinct().Count());
        using (var action = live.Economy.Begin(new(pilots: [id], systems: [StarterSystem])))
        {
            Assert.Null(action.Set<PilotSkill>().Find(id, refining));
            SettlePirateKill(action, catalog, id, StarterSystem, pithEliminator, clock.Now);
            SqliteWorld.SetPosition(action, catalog, action.Pilots.Find(id)!.ActiveShipId, null, null, clock.Now);
            var producer = host.GetRequiredService<SqliteWorld>().FindPilot(action, CapsuleerCommunityStore.MinerAccountId, id)!;
            var plan = new CapsuleerCareerPlan(CapsuleerProfession.Mining, (int)TechOneHullType.Venture, CapsuleerTrainingFocus.Work);
            var turn = host.GetRequiredService<CapsuleerCareerTraining>()
                .Advance(action, catalog, producer, plan.Profession, plan, clock.Now);
            Assert.Equal(CapsuleerTrainingFocus.Ship, turn.Plan!.NextFocus);
            Assert.True(turn.LevelChanged);
            action.Commit();
        }
        var purchase = live.Economy.Inspect(world =>
            world
                .Filter<SavedServicePayment>(payment => payment.PilotId == id)
                .Single(payment => payment.Kind == WalletEntryType.SkillPurchase && payment.ContextId == refining)
        );
        Assert.Equal(-9_750_000, purchase.AmountCents);
        Assert.Contains(Allocations(live, id), entry => entry.TypeId == refining && entry.Points > 0);
    }

    private static long SettlePirateKill(
        Eden.World.Runtime.Economy.WorldAction action,
        CatalogDbContext catalog,
        long id,
        int system,
        int type,
        long now
    )
    {
        new TestNpcPopulationSeeder(system, type).Seed(action, catalog, system, now);
        action.SaveChanges();
        var npc = action.Filter<WorldNpc>(entry => entry.SystemId == system && entry.TypeId == type).OrderBy(entry => entry.Id).First();
        var pilot = action.Pilots.Find(id)!;
        var attributes = catalog.FittingAttributes(type);
        var settlement = NpcDamagePersistence.Apply(
            action,
            catalog,
            npc,
            pilot,
            pilot.ActiveShipId,
            attributes,
            new(100000, 0, 0, 0),
            now,
            (int)CombatWeaponType.CivilianGatlingRailgun
        );
        Assert.True(settlement.Destroyed);
        Assert.NotNull(settlement.Reward);
        long earned = NpcSkillPointRewards.FromBounty(NpcBountyRules.BaseCents(npc.Role, attributes));
        Assert.True(earned > 0);
        return earned;
    }

    private static TrainingLog[] Allocations(LiveWorld live, long id) =>
        live.Economy.Inspect(world =>
            world.Filter<TrainingLog>(entry => entry.PilotId == id).Where(entry => entry.EventId == FreeSkillPointRules.UsedEvent).ToArray()
        );

    private static void CompareWithNewCharacters(ServiceProvider host, LiveWorld live, IReadOnlyList<SavedCapsuleerAgent> founders)
    {
        var launcher = host.GetRequiredService<LauncherStore>();
        var creator = host.GetRequiredService<ICharacterCreation>();
        var inventory = host.GetRequiredService<IPilotInventory>();
        var market = host.GetRequiredService<IPilotMarket>();
        foreach (var founder in founders)
        {
            var bot = live.Economy.Inspect(world => world.Pilots.Find(founder.PilotId)!);
            var account = launcher.Register(new($"Rookie Account {founder.Slot}", "Isolated Test Password 1"));
            long human = creator.Create(
                account.AccountId,
                new(
                    $"Rookie Pilot {founder.Slot}",
                    bot.RaceId,
                    bot.BloodlineId,
                    bot.GenderId,
                    CharacterRules.UnspecifiedAncestry,
                    [],
                    [],
                    bot.SchoolId
                )
            );
            var player = live.Economy.Inspect(world => world.Pilots.Find(human)!);
            Assert.Equal(player.ActiveShip.TypeId, bot.ActiveShip.TypeId);
            Assert.Equal(AccountCloneGrade.Omega, bot.Account.CloneGrade);
            Assert.Equal(0, market.Balance(human));
            Assert.Equal(0, market.Balance(bot.Id));
            Assert.Equal(Factory, bot.ActiveShip.StationId);
            var kit = inventory.List(bot.Id, Factory);
            Assert.Equal(2, kit.Count);
            Assert.Equal(
                inventory
                    .List(human, player.ActiveShipId)
                    .Where(i => InventoryDefinitions.IsHighSlot(i.FlagId))
                    .Select(i => i.TypeId)
                    .Order(),
                kit.Select(i => i.TypeId).Order()
            );
            Assert.Equal(Skills(human), Skills(bot.Id));
        }

        (int, long)[] Skills(long pilot) =>
            live.Economy.Inspect(world =>
                world
                    .Filter<PilotSkill>(skill => skill.PilotId == pilot)
                    .OrderBy(skill => skill.TypeId)
                    .Select(skill => new ValueTuple<int, long>(skill.TypeId, skill.Points))
                    .ToArray()
            );
    }

    private void Advance(ServiceProvider host, TestClock clock, Func<bool> completed, int maximumTurns = 100_000)
    {
        var presence = host.GetRequiredService<IHumanPresence>();
        if (!presence.Read().ActiveSystems.Contains(StarterSystem))
        {
            presence.Update(Guid.NewGuid(), StarterSystem);
        }
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        scheduler.PopulationPrepared(StarterSystem);
        for (int turn = 0; turn < maximumTurns; turn++)
        {
            if (turn % 10 == 0 && completed())
            {
                return;
            }
            long due = scheduler.Jobs().Min(job => job.DueTicks);
            long delay = Math.Max(TimeSpan.TicksPerMillisecond, due - presence.Read().ElapsedTicks);
            clock.Milliseconds += (delay + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond;
            scheduler.Run((int)clock.Seconds);
        }
        foreach (var job in scheduler.Jobs())
        {
            var pilot = scheduler.Miner(job.PilotId);
            output.WriteLine(
                $"{job.PilotId} {pilot?.Work.Activity} {pilot?.Work.Trips} trips {pilot?.Work.WaitingReason} at {clock.Seconds}s, "
                    + $"{host.GetRequiredService<IPilotMarket>().Balance(job.PilotId)} cents."
            );
        }
        Assert.Fail("The starter industry loop exceeded its simulated work budget.");
    }
}
