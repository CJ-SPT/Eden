using System.Text.Json;
using System.Text.Json.Nodes;
using Eden.Data.Catalog;
using Eden.Data.World.Economy;
using Eden.Data.World.Insurance;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Gameplay.Economy;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Reprocessing;
using Eden.Protocol.Industry;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Industry;
using Eden.Protocol.Tests.Persistence;
using Eden.Server.Maintenance;
using Eden.World.Catalog;
using Eden.World.Economy;
using Eden.World.Insurance;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Industry.StarterProductionTests.Fixture;

namespace Eden.Protocol.Tests.Economy;

public sealed class EconomicLifecycleTests
{
    private const long Pilot = StarterProductionTests.Producer;
    private const int Station = StarterProductionTests.Home;
    private const int System = StarterProductionTests.System;
    private const int Belt = 40009232;

    private static void AddBelt(Fixture f)
    {
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES (9,'Asteroid Belt',2);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Radius) VALUES (15,'Asteroid Belt',9,1);
            INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z) VALUES (40009232,30000142,15,9,0,0,0);
            INSERT INTO SourceRecord VALUES
                ('client/asteroidcountbytypeidbeltid','40009232','{{"1230":1,"1228":1,"18":1,"20":1,"21":1,"1223":1,"22":1}}');
            """
        );
    }

    [Theory]
    [InlineData(false, 256000, 0.01, 256000, 64000, 1024000)]
    [InlineData(true, 256000, 0.01, 320000, 64000, 1024000)]
    public void ManufacturingUsesPinnedWholeIskComponentRounding(bool alpha, int value, double index, long cost, long tax, long scc)
    {
        Assert.Equal(new ManufacturingCharge(cost, tax, scc), EconomicRules.Manufacturing(value, (decimal)index, alpha));
        Assert.Equal(128, EconomicRules.ReprocessingCharge(11, 2.33m, 0.05m));
        Assert.Equal(113, EconomicRules.ReprocessingCharge(1, 1.125m, 1));
        Assert.Equal(267, EconomicRules.ReprocessingCharge(1, 2.675m, 1));
        Assert.Equal(100, EconomicRules.Manufacturing(200, 0, false).TaxCents);
        Assert.Equal(0, EconomicRules.Manufacturing(200, 0, true).CostCents);
        Assert.Equal(0, EconomicRules.ManufacturingFromClientValue(10000, 0.01, false, 0.949).SecurityCents);
        Assert.Equal(50000, EconomicRules.ManufacturingFromClientValue(10000, 0.01, false, 0.95).SecurityCents);
        Assert.Equal(0, EconomicRules.ReprocessingTax(0.05m, 10));
        Assert.Equal(0.125m, EconomicRules.ReprocessingTax(0.05m, -10));
        Assert.Throws<InvalidDataException>(() =>
            EconomicRules.Value(new Dictionary<int, int> { [34] = 1 }, new Dictionary<int, decimal>())
        );
        Assert.Throws<InvalidDataException>(() => EconomicRules.Validate(new() { RenewalHourUtc = 24 }));
        Assert.Throws<InvalidDataException>(() => EconomicRules.Validate(new() { DefaultManufacturingIndex = -1 }));
        Assert.Throws<InvalidDataException>(() => EconomicRules.Validate(new() { AdjustedPrices = [new(34, 2), new(34, 3)] }));
    }

    [Fact]
    public async Task RenewalRunsWithoutPilotsIsBoundedAndIdempotentAndRefreshesResidentViews()
    {
        using Fixture f = new(resident: true);
        AddBelt(f);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z) VALUES (40009245,30000142,15,9,0,0,0);
                INSERT INTO SourceRecord VALUES ('client/asteroidcountbytypeidbeltid','40009245','{{"1230":1}}');
                """
            );
        }
        var field = f.Db.Services.GetRequiredService<IAsteroidField>();
        var original = field.List(System);
        Assert.Equal(8, original.Count);
        Dictionary<long, long> capacity;
        using (WorldDbContext world = new(f.Db.Path))
        {
            capacity = world.Set<WorldAsteroid>().ToDictionary(a => a.Id, a => a.Quantity);
            world.Database.ExecuteSqlRaw("UPDATE WorldAsteroid SET Quantity=0");
        }
        Assert.Empty(field.List(System));
        var renewal = f.Db.Services.GetRequiredService<IResourceRenewal>();
        Assert.Equal(0, renewal.Renew());
        using (WorldDbContext world = new(f.Db.Path))
        {
            f.Clock.UtcNow = DateTimeOffset.FromFileTime(world.Set<SavedBeltRenewal>().Min(b => b.NextRenewal));
        }
        Assert.Equal(1, renewal.Renew(1));
        Assert.Equal(7, field.List(System).Count);
        var timerClock = new CheckpointClock(f.Clock);
        await using MarketMaintenance maintenance = new(f.Personal, timerClock, renewal);
        maintenance.Start();
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        timerClock.FireDueTimers();
        await maintenance.Pending;
        Assert.Null(maintenance.LastFailure);
        Assert.Equal(0, await Task.Run(() => renewal.Renew(1)));
        Assert.Equal(original, field.List(System));
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.All(world.Set<WorldAsteroid>(), a => Assert.Equal(capacity[a.Id], a.Quantity));
            world.Database.ExecuteSqlRaw("UPDATE WorldAsteroid SET Quantity=0");
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(30));
        while (renewal.Renew(1) > 0)
        {
            // Exercise overdue renewal through the running world's owner.
        }
        Assert.Equal(original, field.List(System));
        Assert.Equal(0, renewal.Renew());
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        string checkpoint = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "renewed-checkpoint.sqlite");
        File.Copy(f.Db.Path, checkpoint);
        WorldInitialization.Initialize(new(checkpoint), f.Clock);
        using (WorldDbContext reopened = new(checkpoint))
        {
            Assert.Equal(8, reopened.Set<WorldAsteroid>().Count());
            Assert.All(reopened.Set<WorldAsteroid>(), a => Assert.Equal(capacity[a.Id], a.Quantity));
            Assert.All(reopened.Set<SavedBeltRenewal>(), b => Assert.True(b.NextRenewal > f.Clock.Now));
        }
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(8, saved.Set<WorldAsteroid>().Count());
        Assert.All(saved.Set<SavedBeltRenewal>(), b => Assert.True(b.NextRenewal > f.Clock.Now));
        saved.Set<SavedEconomicPolicy>().Single().RenewBelts = false;
        saved.Database.ExecuteSqlRaw("UPDATE WorldAsteroid SET Quantity=0");
        saved.SaveChanges();
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        Assert.Equal(0, renewal.Renew());
        Assert.Empty(field.List(System));
    }

    [Fact]
    public void ReprocessingQuotesAndChargesExactlyOnceAndRejectionPreservesMoneyAndOre()
    {
        using Fixture f = new();
        f.EnableServiceCosts();
        f.Mine(1230, 200);
        var reprocessing = f.Db.Services.GetRequiredService<IPilotReprocessing>();
        long ore = f.Stack(Pilot, 1230).Id;
        var quote = Assert.Single(reprocessing.Quote(Pilot, Station, f.Pilot().ShipId, [ore]).Items);
        Assert.True(quote.CostCents > 0);
        long quantity = f.Quantity(Pilot, 1230);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotWallet>().Find(Pilot)!.Cents = quote.CostCents - 1;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() =>
            reprocessing.Reprocess(Pilot, Station, [ore], Station, Pilot, Station, (int)InventoryFlag.Hangar)
        );
        Assert.Equal(quantity, f.Quantity(Pilot, 1230));
        Assert.Equal(quote.CostCents - 1, f.Market.Balance(Pilot));
        Assert.Empty(f.Db.Services.GetRequiredService<IPilotWalletReader>().Journal(Pilot).ServicePayments!);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotWallet>().Find(Pilot)!.Cents = 1_000_000;
            world.SaveChanges();
        }
        var result = reprocessing.Reprocess(Pilot, Station, [ore], Station, Pilot, Station, (int)InventoryFlag.Hangar);
        Assert.Equal(1_000_000 - quote.CostCents, result.BalanceCents);
        Assert.Equal(quote.Recoverables.Single().Quantity, f.Quantity(Pilot, 34));
        Assert.Equal(quote.LeftOvers, f.Quantity(Pilot, 1230));
        Assert.Throws<InvalidDataException>(() =>
            reprocessing.Reprocess(Pilot, Station, [ore], Station, Pilot, Station, (int)InventoryFlag.Hangar)
        );
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        var payment = Assert.Single(f.Db.Services.GetRequiredService<IPilotWalletReader>().Journal(Pilot).ServicePayments!);
        Assert.Equal(WalletEntryType.ReprocessingTax, payment.Kind);
        Assert.Equal(-quote.CostCents, payment.AmountCents);
        Assert.Equal(result.BalanceCents, f.Market.Balance(Pilot));
        var wire = Assert.IsType<BlueInstance>(new Eden.Protocol.Reprocessing.ReprocessingItemReply(quote).ToBlue());
        Assert.Equal(quote.CostCents / 100d, Assert.IsType<Dictionary<string, object?>>(wire.State)["totalISKCost"]);
    }

    [Fact]
    public void ManufacturingValidatesQuoteAndFundsThenPersistsPaidJobWithoutCancellationRefund()
    {
        using Fixture f = new();
        f.EnableServiceCosts();
        var recipe = f.Data.Recipes.Single(r => r.BlueprintId == (int)IndustryBlueprintType.IronChargeSmall);
        var materials = recipe.Materials.ToDictionary(m => m.TypeID, m => m.Quantity);
        f.Gather(materials);
        long blueprint = f.BuyInput(recipe.BlueprintId);
        var request = new ManufacturingRequest(blueprint, recipe.BlueprintId, Station, 1, 215, recipe.Time * 0.8, materials);
        ManufacturingCharge charge;
        using (WorldDbContext world = new(f.Db.Path))
        {
            charge = EconomicStore.Manufacturing(world, Pilot, System, materials);
        }
        long before = f.Market.Balance(Pilot);
        var inventory = f.Inventory.List(Pilot, Station);
        Assert.Throws<InvalidDataException>(() => f.Industry.Install(Pilot, request with { QuotedCharge = new(0, 0, 0) }));
        Assert.Equal(before, f.Market.Balance(Pilot));
        Assert.Equal(inventory, f.Inventory.List(Pilot, Station));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotWallet>().Find(Pilot)!.Cents = charge.TotalCents - 1;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Industry.Install(Pilot, request with { QuotedCharge = charge }));
        Assert.Equal(inventory, f.Inventory.List(Pilot, Station));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<PilotWallet>().Find(Pilot)!.Cents = before;
            world.SaveChanges();
        }
        var installed = f.Industry.Install(Pilot, request with { QuotedCharge = charge });
        Assert.Equal(charge.TotalCents, installed.Job.CostCents);
        Assert.Equal(before - charge.TotalCents, installed.BalanceCents);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(installed.Job.CostCents, f.Industry.Job(Pilot, installed.Job.Id).CostCents);
        f.Industry.Cancel(Pilot, installed.Job.Id, System);
        Assert.Equal(before - charge.TotalCents, f.Market.Balance(Pilot));
        Assert.Single(
            f.Db.Services.GetRequiredService<IPilotWalletReader>().Journal(Pilot).ServicePayments!,
            p => p.Kind == WalletEntryType.Manufacturing
        );
        var facility = f.Industry.Facility(Pilot, Station);
        Assert.Equal(0.01d, facility.CostIndex);
        Assert.Equal((double)EconomicRules.NpcFacilityTax, facility.Tax);
    }

    [Fact]
    public void ReplacementInsuranceUsesTheCatalogProductRelationshipAfterABlueprintKeyChanges()
    {
        using Fixture fixture = new();
        fixture.EnableServiceCosts();
        var insurance = fixture.Db.Services.GetRequiredService<IPilotInsurance>();
        const int hull = 603;
        const int originalBlueprint = 950;
        const int replacementBlueprint = 999950;
        long originalValue = insurance.Price(hull);
        Assert.Equal(25600000, originalValue);
        using (CatalogDbContext writable = new(fixture.CatalogPath, writable: true))
        {
            var original = writable.Set<SourceRecord>().Find("client/blueprints", JsonSerializer.Serialize(originalBlueprint.ToString()))!;
            var data = JsonNode.Parse(original.Data)!;
            data["blueprintTypeID"] = replacementBlueprint;
            writable.Remove(original);
            writable.Add(
                new SourceRecord
                {
                    Dataset = "client/blueprints",
                    Key = JsonSerializer.Serialize(replacementBlueprint.ToString()),
                    Data = data.ToJsonString(),
                }
            );
            writable.SaveChanges();
        }
        Assert.Equal(originalValue, insurance.Price(hull));
        Assert.Equal<int>([replacementBlueprint], CatalogProvider.Open(fixture.CatalogPath).BlueprintsForProduct(hull));
    }

    [Theory]
    [InlineData(603, 25600000)]
    [InlineData(32880, 13440000)]
    [InlineData(648, 186944000)]
    public void EconomyInsuranceUsesReplacementMaterialsAndCannotFundAnotherLoss(int hull, long value)
    {
        using Fixture f = new();
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            string services = JsonSerializer.Serialize(new { services = new[] { (int)StationService.Insurance, 5, 7, 14, 17 } });
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ({"client/stationoperations"},{"14"},{services})");
        }
        f.EnableServiceCosts();
        var insurance = f.Db.Services.GetRequiredService<IPilotInsurance>();
        Assert.Equal(value, insurance.Price(hull));
        Assert.Equal(value, insurance.Prices([hull])[hull]);
        Assert.Equal(0, insurance.Price(601));
        Assert.True(InsuranceRules.Payout(value, InsuranceLevel.Platinum) - InsuranceRules.Premium(value, InsuranceLevel.Platinum) < value);
        long item;
        using (WorldDbContext world = new(f.Db.Path))
        {
            item = InventoryPersistence.NextId(world);
            world.Add(
                new OwnedItem
                {
                    Id = item,
                    OwnerId = StarterProductionTests.Consumer,
                    TypeId = hull,
                    LocationId = Station,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 1,
                }
            );
            world.SaveChanges();
        }
        long ship = Assert.Single(f.Fleet.Assemble(StarterProductionTests.Consumer, [item], null).ShipIds);
        f.Fleet.Board(StarterProductionTests.Consumer, ship, f.Pilot(StarterProductionTests.Consumer).ShipId);
        long before = f.Market.Balance(StarterProductionTests.Consumer);
        long premium = InsuranceRules.Premium(value, InsuranceLevel.Platinum);
        var contract = insurance.Insure(StarterProductionTests.Consumer, Station, ship, premium, false).Policy;
        Assert.Equal(value, contract.ValueCents);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(contract, insurance.Contract(StarterProductionTests.Consumer, ship));
        f.Lose(ship);
        Assert.Equal(before - premium + value, f.Market.Balance(StarterProductionTests.Consumer));
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Single(saved.Set<SavedInsuranceLedger>().Where(p => p.IsPayout));
    }

    [Fact]
    public void PriceInputsRoundTripThroughNativeReplyAndSavedWorld()
    {
        using Fixture f = new();
        f.EnableServiceCosts();
        var prices = f.Db.Services.GetRequiredService<IEconomicPrices>();
        Assert.Equal(2m, prices.Prices().Single(p => p.TypeId == 34).PriceIsk);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(8m, prices.Prices().Single(p => p.TypeId == 35).PriceIsk);
        using PilotCallContext context = new(
            1000001,
            f.Pilot().Account,
            [f.Pilot()],
            p => new(f.Db.Store, p, f.Clock, new(), f.Db.Objects),
            1,
            SignedSettings.Empty
        );
        var handler = f.Db.Services.GetRequiredService<EconomicPriceHandler>();
        var reply = Assert
            .IsType<PilotCallResult.Success>(handler.Call(context, "config", "GetAverageMarketPrices", Array.Empty<object?>(), null))
            .Reply;
        Assert.NotEmpty(BlueMarshal.Encode(reply));
        var row = Assert.IsType<BlueInstance>(new EconomicPriceReply(new(34, 2)).ToBlue());
        Assert.Equal(2d, Assert.IsType<Dictionary<string, object?>>(row.State)["adjustedPrice"]);
    }

    [Fact]
    public void ThreePaidProductionAndTradeCyclesRemainPossibleAfterDepletionRenewalAndReopen()
    {
        using Fixture f = new();
        f.EnableServiceCosts();
        AddBelt(f);
        var field = f.Db.Services.GetRequiredService<IAsteroidField>();
        var rocks = field.List(System);
        long blueprint = f.BuyInput((int)IndustryBlueprintType.IronChargeSmall);
        long initialMoney = f.Market.Balance(Pilot) + f.Market.Balance(StarterProductionTests.Consumer);
        var reprocessing = f.Db.Services.GetRequiredService<IPilotReprocessing>();
        // Claimed production unlocks NPC competition; keep this personal trade cheaper.
        long salePrice = Math.Min(1000, MarketSnapshotCatalog.Quotes[215].SellCents!.Value - 1);
        for (int cycle = 0; cycle < 3; cycle++)
        {
            using (WorldDbContext world = new(f.Db.Path))
            {
                world.Database.ExecuteSqlRaw("UPDATE WorldAsteroid SET Quantity=0 WHERE BeltId=40009232");
            }
            f.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
            WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
            Assert.Equal(rocks, field.List(System));
            foreach (int ore in new[] { 1230, 1228 })
            {
                var rock = rocks.Single(r => r.TypeId == ore);
                f.Mine(ore, 100, rock.Id);
                reprocessing.Reprocess(Pilot, Station, [f.Stack(Pilot, ore).Id], Station, Pilot, Station, (int)InventoryFlag.Hangar);
            }
            var job = f.Industry.Install(
                Pilot,
                new(
                    blueprint,
                    (int)IndustryBlueprintType.IronChargeSmall,
                    Station,
                    1,
                    215,
                    240,
                    new Dictionary<int, int> { [34] = 27, [35] = 21 }
                )
            );
            Assert.True(job.Job.CostCents > 0);
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(240));
            f.Industry.Deliver(Pilot, job.Job.Id, System);
            Assert.Empty(f.Industry.Deliver(Pilot, job.Job.Id, System).Changes);
            f.Personal.PlaceSell(Pilot, [new(f.Stack(Pilot, 215).Id, Station, 215, salePrice, 100)], 1);
            f.Market.Buy(StarterProductionTests.Consumer, Station, [new(215, salePrice, 100)]);
            WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
            Assert.Equal((cycle + 1) * 100, f.Quantity(StarterProductionTests.Consumer, 215));
        }
        Assert.Equal(300, f.Market.History(StarterProductionTests.Consumer, [215]).Sum(h => h.Volume));
        using WorldDbContext saved = new(f.Db.Path);
        var payments = saved.Set<SavedServicePayment>().ToArray();
        Assert.Equal(9, payments.Length);
        Assert.All(payments, p => Assert.True(p.AmountCents < 0));
        long fees = saved.Set<SavedMarketPayment>().Sum(p => p.AmountCents);
        long taxes = f.Market.Transactions(Pilot).Where(t => t.TypeId == 215).Sum(t => t.TaxCents);
        Assert.Equal(
            initialMoney + payments.Sum(p => p.AmountCents) + fees - taxes,
            f.Market.Balance(Pilot) + f.Market.Balance(StarterProductionTests.Consumer)
        );
    }

    [Fact]
    public void NativeInstallAcceptsThePricedQuoteAndNotifiesWalletAndJobChanges()
    {
        using Fixture f = new();
        f.EnableServiceCosts();
        f.Gather(new Dictionary<int, int> { [34] = 27, [35] = 21 });
        long blueprint = f.BuyInput((int)IndustryBlueprintType.IronChargeSmall);
        ManufacturingCharge charge;
        using (WorldDbContext world = new(f.Db.Path))
        {
            charge = EconomicStore.Manufacturing(world, Pilot, System, new Dictionary<int, int> { [34] = 27, [35] = 21 });
        }
        using var scope = f.Db.Services.CreateScope();
        using PilotCallContext context = new(
            1000001,
            f.Pilot().Account,
            [f.Pilot()],
            p => new(f.Db.Store, p, f.Clock, new(), f.Db.Objects),
            1,
            SignedSettings.Empty
        );
        context.Select(f.Pilot());
        Dictionary<string, object?> location = new()
        {
            ["itemID"] = (long)Station,
            ["ownerID"] = Pilot,
            ["flagID"] = 4L,
        };
        Dictionary<string, object?> request = new()
        {
            ["inputLocation"] = location,
            ["outputLocation"] = location,
            ["facilityID"] = (long)Station,
            ["characterID"] = Pilot,
            ["activityID"] = 1L,
            ["account"] = new object?[] { Pilot, 1000L },
            ["cost"] = charge.CostCents / 100,
            ["tax"] = charge.TaxCents / 100,
            ["time"] = 240d,
            ["materials"] = new Dictionary<long, object?> { [34] = 27L, [35] = 21L },
            ["blueprintID"] = blueprint,
            ["blueprintTypeID"] = 1130L,
            ["runs"] = 1L,
            ["productTypeID"] = null,
        };
        var handler = scope.ServiceProvider.GetRequiredService<IndustryHandler>();
        long before = f.Market.Balance(Pilot);
        Assert.IsType<PilotCallResult.Success>(handler.Call(context, "industryManager", "InstallJob", [request]));
        Assert.Equal(before - charge.TotalCents, f.Market.Balance(Pilot));
        Assert.Equal((before - charge.TotalCents) / 100d, Assert.Single(context.Notifications.OfType<AccountBalanceChanged>()).Balance);
        Assert.Contains(context.Notifications, n => n.Name == "OnIndustryJob");
        Assert.Single(
            f.Db.Services.GetRequiredService<IPilotWalletReader>().Journal(Pilot).ServicePayments!,
            p => p.Kind == WalletEntryType.Manufacturing
        );
        var wallet = scope.ServiceProvider.GetRequiredService<WalletHandler>();
        var journal = Assert.IsType<PilotCallResult.Success>(
            wallet.Call(context, "account", "GetTransactions", [1000L, null, null, false])
        );
        Assert.NotEmpty(BlueMarshal.Encode(journal.Reply));
    }

    [Fact]
    public void Version88MigrationPreservesPaidHistoryAndSchedulesExistingDepletedBelts()
    {
        using Fixture f = new();
        AddBelt(f);
        f.Db.Services.GetRequiredService<IAsteroidField>().List(System);
        long balance = f.Market.Balance(Pilot);
        var inventory = f.Inventory.List(Pilot, Station);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                UPDATE WorldAsteroid SET Quantity=0;
                DROP TABLE WorldEconomicPolicy; DROP TABLE WorldAdjustedPrice; DROP TABLE WorldManufacturingIndex;
                DROP TABLE WorldBeltRenewal; DROP TABLE WorldServicePayment;
                ALTER TABLE WorldIndustryJob DROP COLUMN CostCents;
                PRAGMA user_version=88;
                """
            );
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.False(saved.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts);
        Assert.Single(saved.Set<SavedBeltRenewal>());
        Assert.All(saved.Set<WorldAsteroid>(), a => Assert.Equal(0, a.Quantity));
        Assert.Equal(balance, f.Market.Balance(Pilot));
        Assert.Equal(inventory, f.Inventory.List(Pilot, Station));
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.All(saved.Set<WorldAsteroid>().AsNoTracking(), a => Assert.True(a.Quantity > 0));
    }

    [Fact]
    public void FreshEconomySavesExplicitInputsWhileExistingWorldRejectsCreationOverrides()
    {
        var directory = TestDirectory.Create("economic-settings-");
        string path = Path.Combine(directory.FullName, "world.sqlite");
        try
        {
            var database = new WorldDatabase(path);
            TestCatalog.Create(database.CatalogPath);
            PassiveCatalogFixture.LoadStarterProduction(database.CatalogPath);
            EconomicConfiguration configuration = new()
            {
                RenewalHourUtc = 7,
                DefaultManufacturingIndex = 0.015m,
                AdjustedPrices = [new(34, 3.21m)],
                ManufacturingIndices = [new(System, 0.025m)],
            };
            WorldInitialization.Initialize(database, economy: new(false, false), economics: configuration);
            using (WorldDbContext saved = new(path))
            {
                Assert.True(saved.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts);
                Assert.Equal(7, saved.Set<SavedEconomicPolicy>().Single().RenewalHourUtc);
                Assert.Equal(3.21m, saved.Set<SavedAdjustedPrice>().Find(34)!.PriceIsk);
                Assert.Equal(0.025m, EconomicStore.Index(saved, System));
                Assert.Empty(saved.Set<TestingWalletGrant>());
            }
            WorldInitialization.Initialize(database);
            Assert.Throws<InvalidDataException>(() => WorldInitialization.Initialize(database, economics: configuration));
            using WorldDbContext reopened = new(path);
            Assert.Equal(3.21m, reopened.Set<SavedAdjustedPrice>().Find(34)!.PriceIsk);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
