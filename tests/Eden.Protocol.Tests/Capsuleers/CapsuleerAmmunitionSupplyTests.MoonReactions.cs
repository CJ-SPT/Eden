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
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAmmunitionSupplyTests
{
    [Fact]
    public async Task BotMinesAnOwnedFractureAndHaulsReprocessedFeedForOnePaidReaction()
    {
        const int moon = 40000991;
        const long athanor = StructureRules.FirstStructureId + 12;
        const int formula = 46169;
        const int cobalt = 16640;
        const int cadmium = 16643;
        const int crystallite = 16655;
        const int fuel = 4051;
        const int reactor = 45537;
        const int reprocessor = 35899;
        const int input = 100;
        const int fuelPerRun = 5;
        const int yield = 200;
        const int otherSystem = 30000142;
        const long capital = 20_000_000;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PrepareCatalog(fixture);
        PassiveCatalogFixture.LoadStructures(fixture.Database.CatalogPath);
        PrepareRefineryCatalog(fixture);
        using (CatalogDbContext edit = new(fixture.Database.CatalogPath, writable: true))
        {
            edit.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemGroup VALUES (427,'Moon Materials',4),(428,'Intermediate Materials',4);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (46169,'Crystallite Alloy Formula',1888),
                    (16640,'Cobalt',427),(16643,'Cadmium',427),(16655,'Crystallite Alloy',428);
                INSERT OR REPLACE INTO SourceRecord VALUES
                    ('client/types','46169','{{"published":1,"volume":0.01}}'),
                    ('client/types','16640','{{"published":1,"volume":0.1}}'),
                    ('client/types','16643','{{"published":1,"volume":0.1}}'),
                    ('client/types','16655','{{"published":1,"volume":0.1}}'),
                    ('sde/npcStations','60015157','{{"operationID":14,"reprocessingEfficiency":0.5,"reprocessingStationsTake":0.05}}'),
                    ('client/blueprints','"46169"','{{"blueprintTypeID":46169,"activities":{{"reaction":{{"time":600,
                        "materials":[{{"typeID":16640,"quantity":100}},{{"typeID":16643,"quantity":100}},{{"typeID":4051,"quantity":5}}],
                        "products":[{{"typeID":16655,"quantity":200}}]}}}}}}');
                """
            );
            foreach (var grade in MoonMiningRules.Composition(moon, 0.4))
            {
                string key = grade.TypeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                const string materials = """{"materials":[{"materialTypeID":16640,"quantity":250}]}""";
                const string ore = """{"published":1,"volume":10,"portionSize":100}""";
                edit.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES('client/types',{key},{ore})");
                edit.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES('client/typematerials',{key},{materials})");
            }
        }
        UseLocalOrders(fixture);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);

        long miner;
        long ship;
        int corporation;
        long extraction;
        long feedCargo;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using (var setup = live.Economy.Begin(TransactionParticipants.None))
            {
                setup.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts = true;
                var roster = setup.Set<SavedCapsuleerAgent>().ToArray();
                miner = roster.Single(a => a.Career == CapsuleerCareer.Mining).PilotId;
                foreach (var agent in roster.Where(a => a.PilotId != miner))
                {
                    setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                }
                setup.Filter<SavedCapsuleerCommunity>(row => row.PilotId == miner).Single().Progress = null;
                ship = setup.Pilots.Find(miner)!.ActiveShipId;
                corporation = Charter(setup, catalog, miner, "Crater Refining", "CRTR", fixture.Clock.Now);
                setup.Set<SavedCorporation>().Find(corporation)!.BalanceCents = capital;
                var point = MoonMiningCatalog.Points(catalog, Home).Single(p => p.MoonId == moon).Position;
                setup.Add(
                    new SavedStructure
                    {
                        Id = athanor,
                        TypeId = CapsuleerIndustry.AthanorType,
                        SystemId = Home,
                        CorporationId = corporation,
                        Name = "Crater Athanor",
                        HasCore = true,
                        State = StructureState.ShieldVulnerable,
                        MoonId = moon,
                        X = point.X,
                        Y = point.Y,
                        Z = point.Z,
                    }
                );
                int slot = StructureRules.FirstServiceSlot;
                foreach (int type in new[] { CapsuleerIndustry.MoonDrillType, reprocessor, reactor })
                {
                    long item = setup.NextItemId();
                    setup.Add(
                        new OwnedItem
                        {
                            Id = item,
                            OwnerId = corporation,
                            LocationId = athanor,
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
                            ItemId = item,
                            StructureId = athanor,
                            Slot = slot,
                            Online = true,
                            NextFuelAt = fixture.Clock.Now + StructureRules.FuelInterval,
                        }
                    );
                    slot++;
                }
                setup.Add(
                    new OwnedItem
                    {
                        Id = setup.NextItemId(),
                        OwnerId = corporation,
                        LocationId = athanor,
                        TypeId = fuel,
                        Quantity = 30_000,
                        FlagId = StructureRules.FuelBayFlag,
                    }
                );
                foreach (int type in new[] { cobalt, formula })
                {
                    setup.Add(new SavedAdjustedPrice { TypeId = type, PriceIsk = 1000 });
                }
                AddStock(setup, miner, Depot, formula, 1);
                AddStock(setup, miner, Depot, cadmium, input);
                AddStock(setup, miner, Depot, fuel, fuelPerRun);
                Assert.Equal(0, Stock(setup, miner, Depot, cobalt));
                setup.Commit();
            }
            var field = host.GetRequiredService<IMoonExtractions>().Start(miner, athanor, MoonMiningRules.MinimumExtractionSeconds);
            extraction = field.Id;
            fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(field.AutoFractureAt - fixture.Clock.Now));
            host.GetRequiredService<IPilotStructures>().Advance();
            Assert.NotNull(live.Economy.Read(world => world.Set<SavedMoonExtraction>().Find(extraction)!.FracturedAt));

            using var online = host.GetRequiredService<IHumanPresence>().Select(Customer, otherSystem);
            AdvanceUntil(host, () => live.MinerCheckpoints.GetValueOrDefault(miner)?.Work.FactoryStage?.Cargo.Length == 4);
            var stage = live.MinerCheckpoints[miner].Work.FactoryStage!;
            Assert.Equal(athanor, stage.StructureId);
            feedCargo = live.Economy.Read(world =>
            {
                var cargo = stage.Cargo.Select(stack => world.Set<OwnedItem>().Find(stack.ItemId)!).ToArray();
                Assert.All(cargo, item => Assert.Equal((ship, (int)InventoryFlag.Cargo), (item.LocationId, item.FlagId)));
                var feed = Assert.Single(cargo, item => item.TypeId == cobalt);
                Assert.Equal(input, feed.Quantity);
                Assert.True(
                    world
                        .Filter<SavedMoonMiningLog>(row => row.StructureId == athanor)
                        .Where(row => row.PilotId == miner)
                        .Sum(row => row.Quantity) >= 100
                );
                Assert.Empty(world.Filter<IndustryJob>(job => job.PilotId == miner));
                return feed.Id;
            });
            Assert.False(live.IsDetailedSystem(Home));
            online.Dispose();
            await live.FlushAsync();
        }

        fixture.Clock.Seconds += 3600;
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        long jobId;
        long cost;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            Assert.Equal(ship, live.Economy.Read(world => world.Set<OwnedItem>().Find(feedCargo)!.LocationId));
            using var online = host.GetRequiredService<IHumanPresence>().Select(Customer, otherSystem);
            AdvanceUntil(host, () => live.Economy.Read(world => world.Filter<IndustryJob>(job => job.PilotId == miner).Any()));
            (jobId, cost) = live.Economy.Read(world =>
            {
                var job = Assert.Single(world.Filter<IndustryJob>(job => job.PilotId == miner));
                Assert.Equal((athanor, IndustryActivity.Reaction, crystallite), (job.StationId, job.Activity, job.ProductTypeId));
                Assert.Equal(IndustryJobStatus.Installed, job.Status);
                Assert.NotNull(job.CapsuleerEndTicks);
                Assert.True(job.CostCents > 0);
                Assert.Equal(0, Stock(world, miner, athanor, cobalt));
                Assert.Equal(0, Stock(world, miner, athanor, cadmium));
                Assert.Equal(0, Stock(world, miner, athanor, fuel));
                Assert.Equal(0, Stock(world, miner, athanor, crystallite));
                Assert.Equal(capital - job.CostCents, world.Set<SavedCorporation>().Find(corporation)!.BalanceCents);
                return (job.Id, job.CostCents);
            });
            online.Dispose();
            await live.FlushAsync();
        }

        fixture.Clock.Seconds += 3600;
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            var industry = host.GetRequiredService<IPilotIndustry>();
            Assert.Equal(IndustryJobStatus.Installed, industry.Job(miner, jobId).Status);
            Assert.Null(host.GetRequiredService<CapsuleerMaintenance>().Tick());
            using var online = host.GetRequiredService<IHumanPresence>().Select(Customer, otherSystem);
            AdvanceUntil(host, () => industry.Job(miner, jobId).Status == IndustryJobStatus.Delivered);
            Assert.Equal(yield, live.Economy.Read(world => Stock(world, miner, athanor, crystallite)));
            Assert.False(live.IsDetailedSystem(Home));
            await live.FlushAsync();
        }
        using WorldDbContext saved = new(fixture.Database.Path);
        Assert.Equal(IndustryJobStatus.Delivered, saved.Set<IndustryJob>().Single(job => job.PilotId == miner).Status);
        var debit = Assert.Single(
            saved.Set<SavedWalletLedger>().Where(row => row.OwnerId == corporation && row.EntryType == WalletEntryType.Manufacturing)
        );
        Assert.Equal(-cost, debit.AmountCents);

        void AdvanceUntil(ServiceProvider host, Func<bool> ready)
        {
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            for (int turn = 0; turn < 2400 && !ready(); turn++)
            {
                fixture.Clock.Seconds++;
                worker.Tick();
            }
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            var state = scheduler.Miner(miner)?.Work;
            Assert.True(ready(), scheduler.Read().LastFailure ?? $"{state?.Activity} at {state?.TargetId}: {state?.WaitingReason}");
        }
    }
}
