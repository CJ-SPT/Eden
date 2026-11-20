using System.Text.Json;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Corporations;
using Eden.Data.World.Economy;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Data.World.Structures;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Structures;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAmmunitionSupplyTests
{
    private const int ChainMoon = 40000991;
    private const long ChainRefinery = StructureRules.FirstStructureId + 12;
    private const int ChainFactorySystem = 30000142;
    private const int ChainFactoryCommunity = 31000001;
    private const int ChainObserverSystem = 30000143;
    private const int ChainFuelBlock = 4051;
    private const int ChainFuelBlueprint = 4310;
    private const int ChainIce = 16264;
    private const int ChainHeavyWater = 16272;
    private const int ChainCobalt = 16640;
    private const int ChainCadmium = 16643;
    private const int ChainCrystallite = 16655;
    private const int ChainCarbonide = 16670;
    private const int ChainPhoton = 11541;
    private const int ChainLaserCrystals = 11689;
    private const int ChainCrystalliteFormula = 46169;
    private const int ChainCarbonideFormula = 46205;
    private const int ChainReactor = 45537;
    private const long ChainBidPrice = 250_000_000;

    [Fact]
    public async Task BotsDeliverTwoMinerTwoBatchesAcrossRestartsAndReplaceBootstrapReactionFuel()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PassiveCatalogFixture.LoadMining(fixture.Database.CatalogPath);
        PrepareCatalog(fixture);
        PrepareMinerTwoCatalog(fixture);
        using (var provisioner = new CapsuleerProvisioner(fixture.Database, fixture.Clock))
        {
            provisioner.EnsureSystem(ChainFactoryCommunity);
        }
        PassiveCatalogFixture.LoadStructures(fixture.Database.CatalogPath);
        PrepareRefineryCatalog(fixture);
        PrepareMoonChainCatalog(fixture);
        UseLocalOrders(fixture);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);

        long miner;
        long factory;
        long courier;
        int corporation;
        long reaction;
        long reactor;
        long firstExtraction;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using var catalog = new CatalogDbContext(fixture.Database.CatalogPath);
            using (var setup = live.Economy.Begin(TransactionParticipants.None))
            {
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
                setup.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts = true;
                var agents = setup.Set<SavedCapsuleerAgent>().OrderBy(agent => agent.PilotId).ToArray();
                miner = agents.First(agent => agent.Career == CapsuleerCareer.Mining && agent.CommunitySystemId == Home).PilotId;
                factory = agents
                    .First(agent => agent.Career == CapsuleerCareer.Mining && agent.CommunitySystemId == ChainFactoryCommunity)
                    .PilotId;
                courier = agents.First(agent => agent.Career == CapsuleerCareer.Hauling).PilotId;
                foreach (var agent in agents.Where(agent => agent.PilotId != miner && agent.PilotId != factory && agent.PilotId != courier))
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                }
                foreach (var (pilot, station) in new[] { (miner, Depot), (factory, FactoryStation), (courier, FactoryStation) })
                {
                    var ship = setup.Pilots.Find(pilot)!.ActiveShip;
                    ship.StationId = station;
                    if (setup.Positions.Find(ship.Id) is { } position)
                    {
                        setup.Remove(position);
                    }
                    if (pilot == courier)
                    {
                        setup.Filter<SavedCapsuleerHauler>(row => row.PilotId == pilot).Single().Progress = null;
                    }
                    else
                    {
                        var community = setup.Filter<SavedCapsuleerCommunity>(row => row.PilotId == pilot).Single();
                        community.HomeStationId = station;
                        community.Progress = null;
                    }
                }
                setup.Pilots.Find(courier)!.ActiveShip.TypeId = HaulingRules.BadgerType;
                setup.Pilots.Find(Customer)!.ActiveShip.StationId = FactoryStation;
                var wallet = setup.Set<PilotWallet>().Find(Customer) ?? setup.Add(new PilotWallet { PilotId = Customer }).Entity;
                wallet.Cents = ChainBidPrice * 4;
                corporation = Charter(setup, catalog, factory, "Moon Instruments", "MONI", fixture.Clock.Now);
                CorporationMembershipStore.Move(setup, setup.Pilots.Find(miner)!, corporation, fixture.Clock.Now);
                CorporationMembershipStore.Move(setup, setup.Pilots.Find(courier)!, corporation, fixture.Clock.Now);
                setup.Set<SavedCorporation>().Find(corporation)!.BalanceCents = ChainBidPrice;
                SetMinerTwoAdjustedPrices(setup, catalog);
                foreach (
                    int type in new[]
                    {
                        ChainCobalt,
                        ChainCadmium,
                        ChainFuelBlock,
                        ChainCrystalliteFormula,
                        ChainCarbonideFormula,
                        ChainCarbonide,
                        ChainHeavyWater,
                    }
                )
                {
                    if (setup.Set<SavedAdjustedPrice>().Find(type) is null)
                    {
                        setup.Add(new SavedAdjustedPrice { TypeId = type, PriceIsk = 1000 });
                    }
                }
                var moon = MoonMiningCatalog.Points(catalog, Home).Single(point => point.MoonId == ChainMoon).Position;
                setup.Add(
                    new SavedStructure
                    {
                        Id = ChainRefinery,
                        TypeId = CapsuleerIndustry.AthanorType,
                        SystemId = Home,
                        CorporationId = corporation,
                        Name = "Moon Instruments Refinery",
                        HasCore = true,
                        MoonId = ChainMoon,
                        State = StructureState.ShieldVulnerable,
                        X = moon.X,
                        Y = moon.Y,
                        Z = moon.Z,
                    }
                );
                int slot = StructureRules.FirstServiceSlot;
                foreach (int type in new[] { CapsuleerIndustry.MoonDrillType, 35899, ChainReactor })
                {
                    long module = setup.NextItemId();
                    setup.Add(
                        new OwnedItem
                        {
                            Id = module,
                            OwnerId = corporation,
                            LocationId = ChainRefinery,
                            TypeId = type,
                            Quantity = 1,
                            FlagId = slot,
                            Singleton = true,
                            Online = true,
                        }
                    );
                    setup.Add(
                        new SavedStructureService
                        {
                            ItemId = module,
                            StructureId = ChainRefinery,
                            Slot = slot++,
                            Online = true,
                            NextFuelAt = fixture.Clock.Now + StructureRules.FuelInterval,
                        }
                    );
                }
                setup.Add(
                    new OwnedItem
                    {
                        Id = setup.NextItemId(),
                        OwnerId = corporation,
                        LocationId = ChainRefinery,
                        TypeId = ChainFuelBlock,
                        Quantity = 480,
                        FlagId = StructureRules.FuelBayFlag,
                    }
                );
                AddStock(setup, miner, Depot, ChainFuelBlock, 2);
                AddStock(setup, miner, Depot, ChainCadmium, 200);
                AddStock(setup, miner, Depot, ChainIce, 1);
                foreach (int type in new[] { ChainFuelBlueprint, ChainCrystalliteFormula, ChainCarbonideFormula })
                {
                    AddStock(setup, miner, Depot, type, 1);
                }
                foreach (int type in new[] { CapsuleerMinerTwoRules.Original, 17357, 17348 })
                {
                    AddStock(setup, factory, FactoryStation, type, 1);
                }
                foreach (var (type, quantity) in new[] { (11399, 10), (3689, 30), (483, 10), (11482, 10), (20413, 20), (20424, 20) })
                {
                    AddStock(setup, factory, FactoryStation, type, quantity);
                }
                setup.Commit();
            }
            var extraction = host.GetRequiredService<IMoonExtractions>()
                .Start(factory, ChainRefinery, MoonMiningRules.MinimumExtractionSeconds);
            firstExtraction = extraction.Id;
            fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(extraction.AutoFractureAt - fixture.Clock.Now));
            host.GetRequiredService<IPilotStructures>().Advance();
            var market = host.GetRequiredService<IPersonalMarket>();
            market.PlaceBuy(Customer, new(FactoryStation, 482, ChainBidPrice, 1, 7));
            using (host.GetRequiredService<IHumanPresence>().Select(Customer, ChainObserverSystem))
            {
                Advance(
                    host,
                    () =>
                        live.Economy.Read(world =>
                            world
                                .Filter<IndustryJob>(job => job.PilotId == miner)
                                .Any(job => job.Activity == IndustryActivity.Reaction && job.Status == IndustryJobStatus.Installed)
                        )
                );
                reaction = live.Economy.Read(world =>
                    world.Filter<IndustryJob>(job => job.PilotId == miner).Single(job => job.Activity == IndustryActivity.Reaction).Id
                );
            }
            reactor = live.Economy.Read(world =>
                world.Filter<OwnedItem>(item => item.LocationId == ChainRefinery).Single(item => item.TypeId == ChainReactor).Id
            );
            host.GetRequiredService<IPilotStructures>().SetServiceOnline(factory, ChainRefinery, reactor, false);
            await live.FlushAsync();
        }

        fixture.Clock.AdvanceWallTime(TimeSpan.FromHours(1));
        long cargo;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            Assert.Equal(IndustryJobStatus.Installed, live.Economy.Read(world => world.Set<IndustryJob>().Find(reaction)!.Status));
            Assert.NotNull(live.Economy.Read(world => world.Set<IndustryJob>().Find(reaction)!.CapsuleerSuspendedTicks));
            host.GetRequiredService<IPilotStructures>().SetServiceOnline(factory, ChainRefinery, reactor, true);
            using (host.GetRequiredService<IHumanPresence>().Select(Customer, ChainObserverSystem))
            {
                Advance(
                    host,
                    () =>
                        live.HaulerCheckpoints.GetValueOrDefault(courier)?.Work
                            is { CorporationSupply.FactorySupply.MoonSupply: not null, Freight.Stacks.Length: > 0 }
                );
                cargo = live.HaulerCheckpoints[courier].Work.Freight!.Stacks[0].ItemId;
                Assert.Equal(ChainCarbonide, live.Economy.Read(world => world.Set<OwnedItem>().Find(cargo)!.TypeId));
            }
            await live.FlushAsync();
        }

        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            Assert.Equal(courier, live.Economy.Read(world => world.Set<OwnedItem>().Find(cargo)!.OwnerId));
            using (host.GetRequiredService<IHumanPresence>().Select(Customer, ChainObserverSystem))
            {
                Advance(
                    host,
                    () =>
                        live.Economy.Read(world =>
                            world
                                .Filter<IndustryJob>(job => job.PilotId == factory)
                                .Any(job => job.ProductTypeId == 482 && job.Status == IndustryJobStatus.Installed)
                        )
                );
            }
            await live.FlushAsync();
        }

        long firstFieldExpires;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using (host.GetRequiredService<IHumanPresence>().Select(Customer, ChainObserverSystem))
            {
                Advance(host, () => live.Economy.Read(world => Stock(world, Customer, FactoryStation, 482) == 1));
                Assert.Equal(
                    0,
                    live.Economy.Read(world =>
                        Stock(world, miner, Depot, ChainFuelBlock) + Stock(world, miner, ChainRefinery, ChainFuelBlock)
                    )
                );
                Assert.Empty(
                    live.Economy.Read(world =>
                        world.Filter<IndustryJob>(job => job.PilotId == miner).Where(job => job.ProductTypeId == ChainFuelBlock).ToArray()
                    )
                );
            }
            firstFieldExpires = live.Economy.Read(world => world.Set<SavedMoonExtraction>().Find(firstExtraction)!.FieldExpiresAt!.Value);
            await live.FlushAsync();
        }

        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(firstFieldExpires - fixture.Clock.Now + TimeSpan.TicksPerSecond));
        long secondExtraction;
        long secondFracture;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            host.GetRequiredService<IPilotStructures>().Advance();
            Assert.Empty(
                live.Economy.Read(world =>
                    world
                        .Filter<OwnedItem>(item => item.LocationId == ChainRefinery)
                        .Where(item => item.FlagId == StructureRules.FuelBayFlag)
                        .ToArray()
                )
            );
            host.GetRequiredService<IPersonalMarket>().PlaceBuy(Customer, new(FactoryStation, 482, ChainBidPrice, 1, 90));
            using (host.GetRequiredService<IHumanPresence>().Select(Customer, ChainObserverSystem))
            {
                Advance(
                    host,
                    () =>
                        live.Economy.Read(world =>
                            world
                                .Filter<SavedMoonExtraction>(row => row.StructureId == ChainRefinery)
                                .Any(row => row.Id != firstExtraction && !row.Ended)
                        )
                );
                (secondExtraction, secondFracture) = live.Economy.Read(world =>
                {
                    var next = world
                        .Filter<SavedMoonExtraction>(row => row.StructureId == ChainRefinery)
                        .Single(row => row.Id != firstExtraction);
                    Assert.True(
                        world
                            .Filter<OwnedItem>(item => item.LocationId == ChainRefinery)
                            .Any(item => item.FlagId == StructureRules.FuelBayFlag && item.TypeId == ChainFuelBlock)
                    );
                    return (next.Id, next.AutoFractureAt);
                });
            }
            await live.FlushAsync();
        }

        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(secondFracture - fixture.Clock.Now));
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            host.GetRequiredService<IPilotStructures>().Advance();
            Assert.NotNull(live.Economy.Read(world => world.Set<SavedMoonExtraction>().Find(secondExtraction)!.FracturedAt));
            using (host.GetRequiredService<IHumanPresence>().Select(Customer, ChainObserverSystem))
            {
                Advance(host, () => live.Economy.Read(world => Stock(world, Customer, FactoryStation, 482) == 2));
                live.Economy.Inspect(world =>
                {
                    var delivered = world
                        .Filter<IndustryJob>(job => job.OutputCorporationId == corporation)
                        .Where(job => job.Status == IndustryJobStatus.Delivered)
                        .ToArray();
                    foreach (int type in new[] { ChainCrystallite, ChainCarbonide, ChainPhoton, ChainLaserCrystals, 482 })
                    {
                        Assert.Equal(2, delivered.Count(job => job.ProductTypeId == type));
                    }
                    var fuel = delivered.Where(job => job.ProductTypeId == ChainFuelBlock).ToArray();
                    Assert.NotEmpty(fuel);
                    Assert.All(fuel, job => Assert.Contains(ChainHeavyWater.ToString(), job.MaterialsJson));
                    var ledger = world.Filter<SavedWalletLedger>(entry => entry.OwnerId == corporation).ToArray();
                    foreach (var job in delivered.Where(job => job.CostCents > 0))
                    {
                        Assert.Single(ledger, entry => entry.ReferenceId == job.Id && entry.AmountCents == -job.CostCents);
                    }
                    Assert.All(delivered.Where(job => job.Activity == IndustryActivity.Reaction), job => Assert.True(job.CostCents > 0));
                    Assert.Contains(ledger, entry => entry.EntryType == WalletEntryType.ReprocessingTax && entry.AmountCents < 0);
                    Assert.NotEmpty(world.Filter<SavedMoonMiningLog>(row => row.StructureId == ChainRefinery));
                    Assert.Equal(0, Stock(world, miner, Depot, ChainIce));
                    var bids = world
                        .Filter<SavedPersonalOrder>(order => order.PilotId == Customer)
                        .Where(order => order.TypeId == 482 && order.Bid)
                        .ToArray();
                    Assert.Equal(2, bids.Length);
                    Assert.All(bids, bid => Assert.Equal((0, 0L), (bid.Remaining, bid.EscrowCents)));
                    return true;
                });
            }
            await live.FlushAsync();
        }
        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        Assert.Equal(2, reopened.GetRequiredService<LiveWorld>().Economy.Read(world => Stock(world, Customer, FactoryStation, 482)));

        void Advance(ServiceProvider host, Func<bool> done)
        {
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            for (int step = 0; step < 25_000 && !done(); step++)
            {
                fixture.Clock.Seconds++;
                maintenance.Tick();
            }
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            var state = host.GetRequiredService<LiveWorld>();
            var jobs = state.Economy.Read(world =>
                string.Join(
                    ", ",
                    world
                        .Filter<IndustryJob>(job => job.OutputCorporationId == corporation)
                        .AsEnumerable()
                        .Select(job => $"{job.ProductTypeId}:{job.Status}")
                )
            );
            Assert.True(
                done(),
                $"{scheduler.Read().LastFailure}; miner {scheduler.Miner(miner)?.Work.Activity}; "
                    + $"factory {scheduler.Miner(factory)?.Work.Activity}; jobs {jobs}"
            );
        }
    }

    private static void PrepareMoonChainCatalog(CapsuleerHaulerTests.Fixture fixture)
    {
        using var catalog = new CatalogDbContext(fixture.Database.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemGroup VALUES (427,'Moon Materials',4),(428,'Intermediate Materials',4),
                (465,'Ice',25),(423,'Ice Products',4);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (46169,'Crystallite Alloy Formula',1888),
                (16640,'Cobalt',427),(16643,'Cadmium',427),(16655,'Crystallite Alloy',428),
                (17348,'Laser Focusing Crystals Blueprint',134),(4310,'Nitrogen Fuel Block Blueprint',134),
                (16264,'Blue Ice',465),(16272,'Heavy Water',423);
            INSERT OR REPLACE INTO SourceRecord VALUES
                ('sde/npcStations','60015157','{{"operationID":14,"reprocessingEfficiency":0.5,"reprocessingStationsTake":0.05}}'),
                ('client/types','16264','{{"published":1,"volume":1000,"portionSize":1}}'),
                ('client/types','16272','{{"published":1,"volume":0.4}}'),
                ('client/typematerials','16264','{{"materials":[{{"materialTypeID":16272,"quantity":10}}]}}');
            UPDATE SourceRecord SET Data=json_set(Data, '$.activities.copying.time', 100, '$.activities.invention.time', 100,
                '$.activities.invention.products[0].probability', 1)
            WHERE Dataset='client/blueprints' AND Key='"785"';
            UPDATE TypeAttribute SET Value=1 WHERE ItemTypeId IN (45009,35899,45537) AND AttributeId IN (2109,2110);
            """
        );
        const string item = """{"published":1,"volume":0.1,"marketGroupID":1}""";
        foreach (int type in new[] { ChainCobalt, ChainCadmium, ChainCrystallite, ChainFuelBlueprint, ChainCrystalliteFormula, 17348 })
        {
            string key = type.ToString(System.Globalization.CultureInfo.InvariantCulture);
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES('client/types',{key},{item})");
        }
        // Combined moon-feed volume exceeds this Venture's general hold.
        catalog.Database.ExecuteSqlRaw(
            "UPDATE SourceRecord SET Data=json_set(Data,'$.volume',0.4) WHERE Dataset='client/types' AND Key IN ('16640','16643')"
        );
        foreach (var grade in MoonMiningRules.Composition(ChainMoon, 0.4))
        {
            string key = grade.TypeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            const string materials = """{"materials":[{"materialTypeID":16640,"quantity":250}]}""";
            const string ore = """{"published":1,"volume":10,"portionSize":100}""";
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES('client/types',{key},{ore})");
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES('client/typematerials',{key},{materials})");
        }
        Reaction(ChainCrystalliteFormula, ChainCrystallite, 1, [(ChainCobalt, 100), (ChainCadmium, 100), (ChainFuelBlock, 1)]);
        Reaction(ChainCarbonideFormula, ChainCarbonide, 2, [(ChainCrystallite, 1), (ChainFuelBlock, 1)]);
        Recipe(17357, ChainPhoton, 5, "manufacturing", [(ChainCarbonide, 1)]);
        Recipe(17348, ChainLaserCrystals, 3, "manufacturing", [(ChainCarbonide, 1)]);
        Recipe(ChainFuelBlueprint, ChainFuelBlock, 900, "manufacturing", [(ChainHeavyWater, 1)]);

        void Reaction(int formula, int product, int count, (int Type, int Quantity)[] inputs) =>
            Recipe(formula, product, count, "reaction", inputs);

        void Recipe(int blueprint, int product, int count, string activity, (int Type, int Quantity)[] inputs)
        {
            string key = JsonSerializer.Serialize(blueprint.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string source = JsonSerializer.Serialize(
                new
                {
                    blueprintTypeID = blueprint,
                    activities = new Dictionary<string, object>
                    {
                        [activity] = new
                        {
                            time = 100,
                            products = new[] { new { typeID = product, quantity = count } },
                            materials = inputs.Select(part => new { typeID = part.Type, quantity = part.Quantity }),
                        },
                    },
                }
            );
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES('client/blueprints',{key},{source})");
        }
    }
}
