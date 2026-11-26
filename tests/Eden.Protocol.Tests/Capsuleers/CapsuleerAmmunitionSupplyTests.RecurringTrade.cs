using Eden.Data.Catalog.Market;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Corporations;
using Eden.Data.World.Economy;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.Protocol.Tests.Market;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Industry;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ammo = Eden.Gameplay.Capsuleers.CapsuleerAmmunitionSupplyRules;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAmmunitionSupplyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegionalTradePaysForTwoLossesWithBountyTaxAndProductionMargin(bool standardMarket)
    {
        const int factorySystem = 30000142;
        const int railgun = (int)CombatWeaponType.GatlingRailgun75mm;
        const int pirate = 30380;
        const int firstGun = (int)InventoryFlag.HighSlot0;
        const int roundsPerMagazine = 200;
        const int cycles = 2;
        const long bountyIsk = 50_000_000;
        const long supplierCapital = 5_000_000L * MarketDefinitions.CentsPerIsk;
        int hull = (int)(standardMarket ? TechOneHullType.Incursus : TechOneHullType.Merlin);
        int charge = standardMarket ? (int)HybridChargeType.AntimatterSmall : Ammo.ChargeType;
        Dictionary<int, int> parts = new()
        {
            [hull] = 1,
            [railgun] = 2,
            [charge] = 2 * roundsPerMagazine,
        };
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        PrepareRegionalTrade(fixture);
        if (standardMarket)
        {
            PassiveCatalogFixture.LoadWeaponTiers(fixture.Database.CatalogPath, new HashSet<int> { charge });
        }
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemCategory VALUES (11,'Entity');
                INSERT OR IGNORE INTO ItemGroup VALUES (286,'Minor Threat',11),(186,'Wreck',2);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (30380,'Test Pirate',286),(26557,'Frigate Wreck',186);
                INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','30380','{{"wreckTypeID":26557}}'),
                    ('client/types','26557','{{"volume":27500}}');
                """
            );
            if (standardMarket)
            {
                // Build 3662168 manufacturing activities for the Incursus and Antimatter Charge S.
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT OR IGNORE INTO ItemGroup VALUES (105,'Frigate Blueprint',9);
                    INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (941,'Incursus Blueprint',105),
                        (1137,'Antimatter Charge S Blueprint',167);
                    INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','941','{{"published":1,"volume":0.01,"marketGroupID":277}}'),
                        ('client/types','1137','{{"published":1,"volume":0.01,"marketGroupID":307}}'),
                        ('client/blueprints','"941"','{{"blueprintTypeID":941,"maxProductionLimit":30,"activities":{{"manufacturing":{{' ||
                        '"skills":[{{"typeID":3380,"level":1}}],"time":6000,"products":[{{"typeID":594,"quantity":1}}],' ||
                        '"materials":[{{"typeID":34,"quantity":32000}},{{"typeID":35,"quantity":6000}},' ||
                        '{{"typeID":36,"quantity":2500}},{{"typeID":37,"quantity":500}}]}}}}}}'),
                        ('client/blueprints','"1137"','{{"blueprintTypeID":1137,"maxProductionLimit":600,' ||
                        '"activities":{{"manufacturing":{{' ||
                        '"skills":[{{"typeID":3380,"level":1}}],"time":300,"products":[{{"typeID":222,"quantity":100}}],' ||
                        '"materials":[{{"typeID":34,"quantity":204}},{{"typeID":35,"quantity":17}},' ||
                        '{{"typeID":38,"quantity":1}}]}}}}}}'),('client/systems','30000141','{{"securityStatus":-0.2}}');
                    UPDATE SourceRecord SET Data=json_set(Data,'$.security',-0.2)
                        WHERE Dataset='client/solarsystemcontent' AND Key='30000141';
                    """
                );
            }
        }
        ManufacturingRecipeDefinition[] recipes;
        Dictionary<int, int> materials = [];
        LiveMarketOrder[] mineralOrders = [];
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
        {
            recipes = ManufacturingCatalog.Recipes(catalog).Values.Where(recipe => parts.ContainsKey(recipe.ProductTypeId)).ToArray();
            Assert.Equal(parts.Keys.Order(), recipes.Select(recipe => recipe.ProductTypeId).Order());
            foreach (var recipe in recipes)
            {
                int runs = (int)Math.Ceiling(parts[recipe.ProductTypeId] / (double)recipe.OutputQuantity);
                foreach (var material in recipe.Materials)
                {
                    int type = checked((int)material.TypeId!.Value);
                    materials[type] = materials.GetValueOrDefault(type) + checked((int)material.Quantity!.Value) * runs * (cycles + 1);
                }
            }
            if (standardMarket)
            {
                var station = catalog.RequireStation(FactoryStation);
                Assert.NotEqual(catalog.RequireStation(Depot).RegionId, station.RegionId);
                // Best Jita asks in cents from the November 21, 2026 Standard snapshot, with finite fixture volumes.
                Dictionary<int, long> prices = new()
                {
                    [34] = 416,
                    [35] = 1_609,
                    [36] = 5_578,
                    [37] = 21_460,
                    [38] = 77_680,
                };
                mineralOrders = materials
                    .OrderBy(material => material.Key)
                    .Select(
                        (material, index) =>
                            new LiveMarketOrder(
                                index + 1,
                                FactoryStation,
                                station.SolarSystemId,
                                station.RegionId,
                                material.Key,
                                Bid: false,
                                prices[material.Key],
                                material.Value,
                                material.Value,
                                fixture.Clock.Now,
                                MarketDefinitions.NpcOrderDays,
                                MarketDefinitions.StationRange,
                                MinimumVolume: 1
                            )
                    )
                    .ToArray();
                StandardMarketTests.WriteSnapshot(fixture.Database, fixture.Clock, mineralOrders);
            }
        }
        UseLocalOrders(fixture, fundRefits: false, standardMarket: standardMarket, earnedPatrol: standardMarket);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        long producer;
        long ammunitionOperator;
        long courier;
        long patrol;
        int buyer;
        int supplier;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            MarketSale[] offers = [];
            using (var setup = live.Economy.Begin(TransactionParticipants.None))
            {
                Assert.Equal(standardMarket, setup.Settings.Economy.StandardMarket);
                Assert.False(setup.Settings.Capsuleers.FundRefits);
                Assert.Equal(standardMarket, setup.Settings.Capsuleers.EarnedPvpShips);
                var economy = setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!;
                economy.TestingMarket = false;
                economy.TestingGrants = false;
                setup.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts = true;
                var agents = setup.Set<SavedCapsuleerAgent>().OrderBy(agent => agent.PilotId).ToArray();
                producer = agents.Single(agent => agent.Career == CapsuleerCareer.Mining).PilotId;
                var carriers = agents.Where(agent => agent.Career == CapsuleerCareer.Hauling).Take(3).ToArray();
                courier = carriers[0].PilotId;
                patrol = carriers[1].PilotId;
                ammunitionOperator = carriers[2].PilotId;
                setup.Pilots.Find(producer)!.ActiveShip.StationId = FactoryStation;
                var assistant = setup.Pilots.Find(ammunitionOperator)!;
                assistant.ActiveShip.StationId = FactoryStation;
                setup.Pilots.Find(courier)!.ActiveShip.StationId = Depot;
                setup.Pilots.Find(courier)!.ActiveShip.TypeId = (int)FreightHull.Tayra;
                var fighter = setup.Pilots.Find(patrol)!;
                fighter.ActiveShip.StationId = Depot;
                supplier = Charter(setup, catalog, producer, "Repeat Foundry", "RPFW", fixture.Clock.Now);
                buyer = Charter(setup, catalog, courier, "Repeat Patrol", "RPTL", fixture.Clock.Now);
                CorporationMembershipStore.Move(setup, assistant, supplier, fixture.Clock.Now);
                CorporationMembershipStore.Move(setup, fighter, buyer, fixture.Clock.Now);
                Assert.Equal(
                    CapsuleerCorporationRules.BountyTaxRate,
                    setup.Set<SavedCorporation>().Find(buyer)!.Snapshot(0).Details.TaxRate
                );
                Assert.Equal(0, setup.Set<SavedCorporation>().Find(buyer)!.BalanceCents);
                setup.Set<SavedCorporation>().Find(supplier)!.BalanceCents = supplierCapital;
                setup.RemoveRange(setup.Filter<OwnedItem>(item => item.OwnerId == patrol).ToArray());
                TrafficFitting.Apply(
                    setup,
                    catalog,
                    patrol,
                    fighter.ActiveShipId,
                    TrafficSnapshotTests.Profile() with
                    {
                        // The Standard case reproduces the fitted slots and charges in zKill loss 139930491.
                        KillId = standardMarket ? 139930491 : 1001,
                        HullTypeId = hull,
                        Equipment =
                        [
                            new(railgun, firstGun, 1),
                            new(charge, firstGun, standardMarket ? 172 : roundsPerMagazine),
                            new(railgun, firstGun + 1, 1),
                            new(charge, firstGun + 1, standardMarket ? 173 : roundsPerMagazine),
                        ],
                    },
                    fixture.Clock.Now
                );
                if (standardMarket)
                {
                    var organization = CapsuleerCorporations.Organization(setup, patrol);
                    CapsuleerCorporations.SaveOrganization(setup, patrol, organization with { EarnedPvpShipId = fighter.ActiveShipId });
                }
                foreach (
                    var agent in agents.Where(agent =>
                        agent.PilotId != producer
                        && agent.PilotId != ammunitionOperator
                        && agent.PilotId != courier
                        && agent.PilotId != patrol
                    )
                )
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                }
                foreach (long operatorId in new[] { producer, ammunitionOperator, patrol })
                {
                    setup.Set<SavedCapsuleerJob>().Find(operatorId)!.DueTicks = TimeSpan.FromDays(1).Ticks;
                }

                // The external seller supplies only raw inputs. Every finished item in the replay comes from a paid factory job.
                foreach (var recipe in recipes)
                {
                    setup.Add(
                        new OwnedItem
                        {
                            Id = setup.NextItemId(),
                            OwnerId = producer,
                            LocationId = FactoryStation,
                            TypeId = recipe.BlueprintTypeId,
                            FlagId = (int)InventoryFlag.Hangar,
                            Singleton = true,
                            Quantity = 1,
                            BlueprintRuns = -1,
                        }
                    );
                    if (recipe.ProductTypeId == charge)
                    {
                        setup.Add(
                            new OwnedItem
                            {
                                Id = setup.NextItemId(),
                                OwnerId = ammunitionOperator,
                                LocationId = FactoryStation,
                                TypeId = recipe.BlueprintTypeId,
                                FlagId = (int)InventoryFlag.Hangar,
                                Singleton = true,
                                Quantity = 1,
                                BlueprintRuns = -1,
                            }
                        );
                    }
                }
                foreach (int type in materials.Keys)
                {
                    if (setup.Set<SavedAdjustedPrice>().Find(type) is null)
                    {
                        setup.Add(new SavedAdjustedPrice { TypeId = type, PriceIsk = 1 });
                    }
                }
                if (!standardMarket)
                {
                    setup.Pilots.Find(Customer)!.ActiveShip.StationId = FactoryStation;
                    var wallet = setup.Set<PilotWallet>().Find(Customer) ?? setup.Add(new PilotWallet { PilotId = Customer }).Entity;
                    foreach (var (type, quantity) in materials)
                    {
                        AddStock(setup, Customer, FactoryStation, type, quantity);
                    }
                    offers = setup
                        .Filter<OwnedItem>(item => item.OwnerId == Customer)
                        .AsEnumerable()
                        .Where(item => item.LocationId == FactoryStation && materials.ContainsKey(item.TypeId))
                        .Select(item => new MarketSale(item.Id, FactoryStation, item.TypeId, MarketDefinitions.CentsPerIsk, item.Quantity))
                        .ToArray();
                    wallet.Cents += offers.Sum(offer =>
                        PilotMarketStore.SupplyBrokerFee(setup, catalog, Customer, FactoryStation, offer.LimitPriceCents, offer.Quantity)
                    );
                }
                setup.Commit();
            }
            if (offers.Length > 0)
            {
                host.GetRequiredService<IPersonalMarket>().PlaceSell(Customer, offers, 7);
            }
            await live.FlushAsync();
        }

        HashSet<long> allOrders = [];
        HashSet<long> wreckedHulls = [];
        long buyerBalance = 0;
        long sellerBalance = supplierCapital;
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            var ships = host.GetRequiredService<SqliteWorld>();
            var industry = host.GetRequiredService<CapsuleerIndustry>();
            fixture.Clock.AdvanceWallTime(CapsuleerRegionalStockRules.ReviewInterval + TimeSpan.FromSeconds(1));
            long capsule;
            using (var loss = live.Economy.Begin())
            {
                Assert.Empty(loss.Filter<OwnedItem>(item => item.OwnerId == buyer));
                var launch = ships.FindPilot(loss, -1, patrol)!;
                Assert.Equal(cycle == 0, CapsuleerIndustry.CanLaunchOffensiveFleet(loss, catalog, launch, fixture.Clock.Now));
                Assert.Equal(buyerBalance, loss.Set<SavedCorporation>().Find(buyer)!.BalanceCents);
                var fighter = loss.Pilots.Find(patrol)!;
                Assert.Equal(hull, fighter.ActiveShip.TypeId);
                Assert.True(wreckedHulls.Add(fighter.ActiveShipId));
                var station = SystemObjects.Geometry(catalog, Home).Single(body => body.Id == Depot);
                SqliteWorld.SetPosition(loss, catalog, fighter.ActiveShipId, station.Position, Home, fixture.Clock.Now);
                var target = loss.Add(
                    new WorldNpc
                    {
                        Id = NpcEncounterDefinitions.FirstNpcId + 10_000 + cycle,
                        SystemId = Home,
                        TypeId = pirate,
                        Role = NpcRole.Pirate,
                        X = station.Position.X,
                        Y = station.Position.Y,
                        Z = station.Position.Z,
                        UpdatedAt = fixture.Clock.Now,
                    }
                ).Entity;
                var killed = NpcDamagePersistence.Apply(
                    loss,
                    catalog,
                    target,
                    fighter,
                    fighter.ActiveShipId,
                    new Dictionary<DogmaAttributeId, double>
                    {
                        [DogmaAttributeId.StructureHitPoints] = 20,
                        [DogmaAttributeId.EntityKillBounty] = bountyIsk,
                    },
                    new DamageVector(100, 0, 0, 0),
                    fixture.Clock.Now
                );
                Assert.True(killed.Destroyed);
                Assert.Null(killed.Reward);
                capsule = DestroyBotShip(loss, catalog, patrol, fixture.Clock.Now);
                ships.Dock(loss, catalog, patrol, capsule, Depot);
                loss.Commit();
            }
            fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(fixture.Database.Settings.Rewards.BountyTickSeconds));
            host.GetRequiredService<IActivityRewardTicks>().Advance();
            var reward = live.Economy.Inspect(world =>
                world.Filter<NpcBounty>(row => row.PilotId == patrol).OrderByDescending(row => row.Id).First()
            );
            Assert.Equal(bountyIsk * MarketDefinitions.CentsPerIsk, reward.AmountCents);
            Assert.Equal(bountyIsk * MarketDefinitions.CentsPerIsk / 10, reward.TaxCents);

            using var occupied = host.GetRequiredService<IHumanPresence>().Select(Customer, Home);
            RegionalStockTrip? trip = null;
            Advance(
                host,
                courier,
                observation =>
                {
                    trip = observation.Work.CorporationSupply?.RegionalStock;
                    return trip?.OrderIds?.Length == parts.Count;
                }
            );
            var orderIds = trip!.OrderIds!;
            Assert.All(orderIds, id => Assert.True(allOrders.Add(id)));
            live.Economy.Inspect(world =>
            {
                Assert.Equal(capsule, world.Pilots.Find(patrol)!.ActiveShipId);
                Assert.All(
                    orderIds,
                    id =>
                    {
                        var bid = world.Set<SavedPersonalOrder>().Find(id)!;
                        Assert.Equal((buyer, FactoryStation, PersonalOrderStatus.Open), (bid.CorporationId, bid.StationId, bid.Status));
                        Assert.True(bid.EscrowCents > 0);
                    }
                );
                return true;
            });
            for (
                int attempt = 0;
                attempt < 12 && live.Economy.Read(world => orderIds.Any(id => world.Set<SavedPersonalOrder>().Find(id)!.Remaining > 0));
                attempt++
            )
            {
                using var work = live.Economy.Begin(TransactionParticipants.None);
                industry.Produce(work, catalog, ships.FindPilot(work, -1, producer)!, fixture.Clock.Now);
                if (orderIds.Any(id => work.Set<SavedPersonalOrder>().Find(id)!.Remaining > 0))
                {
                    industry.Produce(work, catalog, ships.FindPilot(work, -1, ammunitionOperator)!, fixture.Clock.Now);
                }
                var jobs = work.Filter<IndustryJob>(job => job.OutputCorporationId == supplier)
                    .Where(job => job.Status == IndustryJobStatus.Installed)
                    .ToArray();
                if (cycle == 0 && attempt == 0)
                {
                    Assert.Equal(2, jobs.Length);
                    Assert.Contains(jobs, job => job.PilotId == producer && job.ProductTypeId == hull);
                    Assert.Contains(jobs, job => job.PilotId == ammunitionOperator && job.ProductTypeId == charge);
                }
                long ready = jobs.Select(job => job.EndTime).DefaultIfEmpty(fixture.Clock.Now).Min();
                work.Commit();
                if (ready > fixture.Clock.Now)
                {
                    fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(ready - fixture.Clock.Now));
                }
            }
            live.Economy.Inspect(world =>
            {
                var bids = orderIds.Select(id => world.Set<SavedPersonalOrder>().Find(id)!).ToArray();
                Assert.Equal(
                    parts.Select(part => (part.Key, Quantity: part.Value * (cycle + 1))).Order(),
                    bids.Select(bid => (bid.TypeId, Quantity: bid.Entered)).Order()
                );
                var jobs = world
                    .Filter<IndustryJob>(job => job.OutputCorporationId == supplier)
                    .AsEnumerable()
                    .Select(job => $"{job.ProductTypeId} x{job.OutputQuantity} {job.Status}");
                Assert.True(
                    bids.All(bid => bid.Remaining == 0),
                    $"Cycle {cycle}: {string.Join(", ", bids.Select(bid => $"{bid.TypeId} x{bid.Remaining} {bid.Status}"))}. "
                        + $"Jobs: {string.Join(", ", jobs)}"
                );
                return true;
            });
            Advance(
                host,
                courier,
                observation => observation.Work.CorporationSupply is null && observation.Pilot is { StationId: Depot, Position: null }
            );
            using (var recover = live.Economy.Begin())
            {
                var kit = recover
                    .Filter<OwnedItem>(item => item.OwnerId == buyer)
                    .Where(item => item.LocationId == Depot && item.FlagId == CorporationStorageRules.MarketDeliveriesFlag)
                    .ToArray();
                Assert.Equal(
                    parts.Select(part => (part.Key, Quantity: part.Value * (cycle + 1))).Order(),
                    kit.Select(item => (item.TypeId, item.Quantity)).Order()
                );
                var fighter = ships.FindPilot(recover, -1, patrol)!;
                var transfer = industry.ReplaceShip(recover, catalog, fighter, CapsuleerCareer.Hauling, fixture.Clock.Now, null, null);
                Assert.False(transfer.Boarded);
                Assert.Empty(Assert.IsType<long[]>(transfer.OrderIds));
                var replacement = industry.ReplaceShip(
                    recover,
                    catalog,
                    fighter,
                    CapsuleerCareer.Hauling,
                    fixture.Clock.Now,
                    transfer.OrderDeadline,
                    transfer.OrderIds
                );
                Assert.True(replacement.Boarded);
                var newHull = recover.Pilots.Find(patrol)!.ActiveShipId;
                Assert.Equal(hull, recover.Ships.Find(newHull)!.TypeId);
                Assert.DoesNotContain(newHull, wreckedHulls);
                Assert.Null(recover.Ships.Find(capsule));
                Assert.Equal(
                    2,
                    recover.Filter<OwnedItem>(item => item.LocationId == newHull).Count(item => item.Online && item.TypeId == railgun)
                );
                foreach (var (type, quantity) in parts)
                {
                    Assert.Equal(cycle * quantity, Stock(recover, buyer, Depot, type));
                }
                var staged = ships.FindPilot(recover, -1, patrol)!;
                Assert.Equal(cycle > 0, CapsuleerIndustry.CanLaunchOffensiveFleet(recover, catalog, staged, fixture.Clock.Now));
                Assert.True(
                    CapsuleerIndustry.CanLaunchOffensiveFleet(
                        recover,
                        catalog,
                        staged,
                        fixture.Clock.Now + CapsuleerRegionalStockRules.FleetLossMemory.Ticks
                    )
                );
                long paidBuyer = recover.Set<SavedCorporation>().Find(buyer)!.BalanceCents;
                long paidSeller = recover.Set<SavedCorporation>().Find(supplier)!.BalanceCents;
                Assert.True(paidBuyer > buyerBalance);
                Assert.True(paidSeller > sellerBalance);
                buyerBalance = paidBuyer;
                sellerBalance = paidSeller;
                recover.Commit();
                if (standardMarket && cycle == cycles - 1)
                {
                    // The fixture holds the patrol's routine until paid recovery is complete.
                    long due = host.GetRequiredService<IHumanPresence>().Read().ElapsedTicks + TimeSpan.TicksPerSecond;
                    live.CapsuleerJobs[patrol] = live.CapsuleerJobs[patrol].Next(due, CapsuleerAction.Trade);
                }
            }
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        reopened
            .GetRequiredService<LiveWorld>()
            .Economy.Inspect(world =>
            {
                Assert.Equal(cycles, world.Filter<ShipLoss>(loss => loss.PilotId == patrol).Count());
                Assert.Equal(cycles, CapsuleerCorporations.Organization(world, patrol).FleetLosses?.Length);
                Assert.All(parts, part => Assert.Equal(part.Value, Stock(world, buyer, Depot, part.Key)));
                Assert.Equal(cycles, world.Filter<NpcBounty>(bounty => bounty.PilotId == patrol).Count());
                Assert.Equal(
                    allOrders.Order(),
                    world.Filter<SavedPersonalOrder>(order => order.CorporationId == buyer).Select(order => order.Id).Order()
                );
                Assert.All(
                    allOrders,
                    id =>
                        Assert.Equal(
                            (PersonalOrderStatus.Filled, 0L),
                            (world.Set<SavedPersonalOrder>().Find(id)!.Status, world.Set<SavedPersonalOrder>().Find(id)!.EscrowCents)
                        )
                );
                var productionJobs = world
                    .Filter<IndustryJob>(job => job.OutputCorporationId == supplier)
                    .Where(job => job.Status == IndustryJobStatus.Delivered && job.CostCents > 0)
                    .ToLookup(job => job.ProductTypeId);
                Assert.All(
                    parts,
                    part => Assert.True(productionJobs[part.Key].Sum(job => job.OutputQuantity) >= part.Value * (cycles + 1))
                );
                var production = world.Filter<SavedWalletLedger>(entry => entry.OwnerId == supplier).ToArray();
                foreach (var source in mineralOrders)
                {
                    var order = Assert.Single(
                        world.Filter<NpcMarketOrder>(order => order.TypeId == source.TypeId),
                        order => order.Kind == NpcMarketKind.Standard && order.SourceOrderId == source.SourceOrderId
                    );
                    var fills = world
                        .Filter<MarketExecution>(row => row.OrderId == order.Id)
                        .Where(row => row.IsBuy && (row.PilotId == producer || row.PilotId == ammunitionOperator))
                        .ToArray();
                    int used = fills.Sum(row => row.Quantity);
                    Assert.InRange(used, 1, source.Entered);
                    Assert.Equal(source.Entered - used, order.Remaining);
                    Assert.All(
                        fills,
                        row => Assert.Equal((source.TypeId, FactoryStation, source.PriceCents), (row.TypeId, row.StationId, row.PriceCents))
                    );
                    Assert.Equal(
                        -used * source.PriceCents,
                        production
                            .Where(entry => entry.EntryType == WalletEntryType.MarketTransaction && entry.ReferenceId == order.Id)
                            .Sum(entry => entry.AmountCents)
                    );
                }
                var income = world.Filter<SavedWalletLedger>(entry => entry.OwnerId == buyer).ToArray();
                Assert.Equal(cycles, income.Count(entry => entry.EntryType == CorporationWalletRules.BountyTaxEntry));
                Assert.All(
                    income.Where(entry => entry.AmountCents > 0),
                    entry => Assert.Equal(CorporationWalletRules.BountyTaxEntry, entry.EntryType)
                );
                Assert.Equal(income.Sum(entry => entry.AmountCents), buyerBalance);
                Assert.Contains(production, entry => entry.EntryType == WalletEntryType.MarketTransaction && entry.AmountCents < 0);
                Assert.Contains(production, entry => entry.EntryType == WalletEntryType.BrokersFee && entry.AmountCents < 0);
                Assert.Contains(production, entry => entry.EntryType == WalletEntryType.Manufacturing && entry.AmountCents < 0);
                Assert.All(
                    production.Where(entry => entry.AmountCents > 0),
                    entry => Assert.Equal((WalletEntryType.MarketTransaction, (long)buyer), (entry.EntryType, entry.CounterpartyId))
                );
                Assert.Equal(supplierCapital + production.Sum(entry => entry.AmountCents), sellerBalance);
                if (standardMarket)
                {
                    long replacement = world.Pilots.Find(patrol)!.ActiveShipId;
                    Assert.Equal(replacement, CapsuleerCorporations.Organization(world, patrol).EarnedPvpShipId);
                    using var catalog = new CatalogDbContext(fixture.Database.CatalogPath);
                    Assert.True(CapsuleerIndustry.CanStagePvp(world, catalog, Home));
                    var fit = CapsuleerDefenseProfile.Read(world, catalog, patrol, null);
                    Assert.True(fit.Combat);
                    Assert.Contains(fit.RattingWeapons, weapon => weapon.AvailableShots > 0);
                }
                return true;
            });
        if (standardMarket)
        {
            using var selected = reopened.GetRequiredService<IHumanPresence>().Select(Customer, Home);
            Advance(
                reopened,
                patrol,
                observation => observation.Pilot.Position is not null && observation.Work.Ratting?.Patrol?.Kind == PatrolKind.Roam
            );
            var launched = Assert.IsType<HaulerObservation>(reopened.GetRequiredService<CapsuleerScheduler>().Hauler(patrol));
            Assert.DoesNotContain(launched.Pilot.ShipId, wreckedHulls);
            reopened
                .GetRequiredService<LiveWorld>()
                .Economy.Inspect(world =>
                {
                    Assert.All(parts, part => Assert.Equal(part.Value, Stock(world, buyer, Depot, part.Key)));
                    return true;
                });
        }

        void Advance(ServiceProvider host, long pilot, Func<HaulerObservation, bool> ready)
        {
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            scheduler.PopulationPrepared(Home);
            scheduler.PopulationPrepared(factorySystem);
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            for (int turn = 0; turn < 1600; turn++)
            {
                fixture.Clock.Seconds++;
                maintenance.Tick();
                if (scheduler.Hauler(pilot) is { } observed && ready(observed))
                {
                    return;
                }
            }
            var last = scheduler.Hauler(pilot);
            Assert.Fail(
                $"Pilot {pilot} at {last?.Pilot.SystemId}/{last?.Pilot.StationId}, {last?.Work.Activity}, {last?.Work.WaitingReason}. "
                    + $"Job {scheduler.Jobs().SingleOrDefault(job => job.PilotId == pilot)}. {scheduler.Read().LastFailure}."
            );
        }
    }
}
