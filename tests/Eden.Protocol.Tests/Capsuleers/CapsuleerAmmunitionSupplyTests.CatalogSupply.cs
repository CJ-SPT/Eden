using Eden.Data.Catalog;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Corporations;
using Eden.Data.World.Economy;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Industry;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAmmunitionSupplyTests
{
    [Fact]
    public async Task CatalogDemandCopiesAndInventsThenManufacturesAMissingComponentAcrossRestart()
    {
        const int original = 1032;
        const int invented = 1033;
        const int component = 399;
        const int finished = 400;
        const long price = 100_000_000;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PassiveCatalogFixture.LoadStarterProduction(fixture.Database.CatalogPath);
        PrepareCatalog(fixture);
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true))
        {
            // The build 3662168 recipe uses the Tech I booster. Keep that dependency and control the other inputs and rolls.
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) SELECT 400,'Small Shield Booster II',GroupId FROM ItemType WHERE Id=399;
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) SELECT 1033,'Small Shield Booster II Blueprint',GroupId
                    FROM ItemType WHERE Id=1032;
                INSERT OR REPLACE INTO SourceRecord VALUES
                    ('client/stationoperations','14','{{"services":[5,7,14,15,17]}}'),
                    ('client/typelist','799','{{"includedTypeIDs":[]}}'),
                    ('client/types','399','{{"published":1,"marketGroupID":1,"volume":5,"basePrice":100000}}'),
                    ('client/types','400','{{"published":1,"marketGroupID":1,"volume":5,"basePrice":100000}}'),
                    ('client/types','1033','{{"published":1,"volume":0.01}}'),
                    ('client/blueprints','"1033"','{{"blueprintTypeID":1033,"activities":{{"manufacturing":{{"time":30,
                        "materials":[{{"typeID":399,"quantity":1}}],"products":[{{"typeID":400,"quantity":2}}]}}}}}}');
                UPDATE SourceRecord SET Data=json_set(Data,'$.maxProductionLimit',100,'$.activities.copying',json('{{"time":30}}'),
                    '$.activities.manufacturing.materials',json('[{{"typeID":34,"quantity":2}}]'),
                    '$.activities.invention',json('{{"time":30,"products":[{{"typeID":1033,"quantity":1,"probability":1}}]}}'))
                    WHERE Dataset='client/blueprints' AND Key='"1032"';
                """
            );
        }
        UseLocalOrders(fixture);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);

        long producer;
        int corporation;
        long inventionEnd;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            Assert.Contains(invented, ManufacturingCatalog.Recipes(catalog).Keys);
            var source = catalog
                .Definitions.Pin(CatalogData.Client.Blueprints)
                .Values.Single(blueprint => blueprint.BlueprintTypeID == original);
            Assert.Equal(invented, Assert.Single(source.Activities!.Invention!.Products!.Value).TypeID);
            using (var setup = live.Economy.Begin(TransactionParticipants.None))
            {
                var policy = setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!;
                policy.TestingMarket = false;
                policy.TestingGrants = false;
                setup.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts = false;
                producer = setup.Set<SavedCapsuleerAgent>().Single(agent => agent.Career == CapsuleerCareer.Mining).PilotId;
                setup.Pilots.Find(producer)!.ActiveShip.StationId = FactoryStation;
                setup.Pilots.Find(Customer)!.ActiveShip.StationId = FactoryStation;
                corporation = Charter(setup, catalog, producer, "Shield Supply", "SHSUP", fixture.Clock.Now);
                setup.Set<SavedCorporation>().Find(corporation)!.BalanceCents = price;
                setup.Add(new PilotWallet { PilotId = Customer, Cents = 2 * price });
                AddStock(setup, producer, FactoryStation, original, 1);
                var blueprint = setup.Filter<OwnedItem>(item => item.OwnerId == producer).Single(item => item.TypeId == original);
                blueprint.Singleton = true;
                blueprint.BlueprintRuns = -1;
                foreach (var material in ManufacturingCatalog.Recipes(catalog)[original].Materials)
                {
                    AddStock(setup, producer, FactoryStation, (int)material.TypeId!.Value, (int)material.Quantity!.Value);
                }
                setup.Commit();
            }

            host.GetRequiredService<IPersonalMarket>().PlaceBuy(Customer, new(FactoryStation, finished, price, 1, 7));
            long copyEnd = RunFactory(host, catalog, IndustryActivity.Copying, original);
            fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(copyEnd - fixture.Clock.Now));
            inventionEnd = RunFactory(host, catalog, IndustryActivity.Invention, invented);
            await live.FlushAsync();
        }

        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(inventionEnd - fixture.Clock.Now));
        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using CatalogDbContext restoredCatalog = new(fixture.Database.CatalogPath);
        long componentEnd = RunFactory(reopened, restoredCatalog, IndustryActivity.Manufacturing, component);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(componentEnd - fixture.Clock.Now));
        long manufactureEnd = RunFactory(reopened, restoredCatalog, IndustryActivity.Manufacturing, finished);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(manufactureEnd - fixture.Clock.Now));
        var restored = reopened.GetRequiredService<LiveWorld>();
        using (var delivery = restored.Economy.Begin(TransactionParticipants.None))
        {
            var pilot = reopened.GetRequiredService<SqliteWorld>().FindPilot(delivery, -1, producer)!;
            Assert.True(reopened.GetRequiredService<CapsuleerIndustry>().Produce(delivery, restoredCatalog, pilot, fixture.Clock.Now));
            Assert.Equal(1, Stock(delivery, Customer, FactoryStation, finished));
            Assert.Equal(1, Stock(delivery, corporation, FactoryStation, finished));
            Assert.Equal(0, Stock(delivery, producer, FactoryStation, component));
            Assert.Equal(0, Stock(delivery, corporation, FactoryStation, invented));
            Assert.Equal(
                4,
                delivery
                    .Set<IndustryJob>()
                    .Count(job => job.OutputCorporationId == corporation && job.Status == IndustryJobStatus.Delivered)
            );
            // With the public order already filled, the same workshop builds a spare for an active fit.
            var miner = delivery.Pilots.Find(producer)!;
            miner.ActiveShip.TypeId = (int)CapsuleerMiningHull.Venture;
            long ship = miner.ActiveShipId;
            delivery.Add(
                new OwnedItem
                {
                    Id = delivery.NextItemId(),
                    OwnerId = producer,
                    LocationId = ship,
                    FlagId = (int)InventoryFlag.MediumSlot0,
                    TypeId = component,
                    Quantity = 1,
                    Online = true,
                    Singleton = true,
                }
            );
            foreach (var input in ManufacturingCatalog.Recipes(restoredCatalog)[original].Materials)
            {
                AddStock(delivery, producer, FactoryStation, (int)input.TypeId!.Value, (int)input.Quantity!.Value);
            }
            delivery.Commit();
        }

        long reserveEnd = RunFactory(reopened, restoredCatalog, IndustryActivity.Manufacturing, component);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(reserveEnd - fixture.Clock.Now));
        using (var reserve = restored.Economy.Begin(TransactionParticipants.None))
        {
            var reservePilot = reopened.GetRequiredService<SqliteWorld>().FindPilot(reserve, -1, producer)!;
            reopened.GetRequiredService<CapsuleerIndustry>().Produce(reserve, restoredCatalog, reservePilot, fixture.Clock.Now);
            Assert.Equal(1, Stock(reserve, corporation, FactoryStation, component));
            Assert.Equal(0, Stock(reserve, Customer, FactoryStation, component));
            reserve.Commit();
        }

        long RunFactory(ServiceProvider host, CatalogDbContext catalog, IndustryActivity activity, int product)
        {
            var live = host.GetRequiredService<LiveWorld>();
            using var dock = live.Economy.Begin(TransactionParticipants.None);
            var operatorPilot = host.GetRequiredService<SqliteWorld>().FindPilot(dock, -1, producer)!;
            host.GetRequiredService<CapsuleerIndustry>().Produce(dock, catalog, operatorPilot, fixture.Clock.Now);
            var job = Assert.Single(dock.Set<IndustryJob>(), job => job.Status == IndustryJobStatus.Installed);
            Assert.Equal((activity, product, (int?)corporation), (job.Activity, job.ProductTypeId, job.OutputCorporationId));
            dock.Commit();
            return job.EndTime;
        }
    }
}
