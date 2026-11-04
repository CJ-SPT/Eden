using System.Text.Json.Nodes;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Market;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

internal static class InsuranceTestData
{
    internal static void Prepare(TestWorld db, int typeId = 601, double value = 1000)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        var source = catalog
            .Set<SourceRecord>()
            .AsTracking()
            .SingleOrDefault(record => record.Dataset == "client/types" && record.Key == typeId.ToString());
        var data = JsonNode.Parse(source?.Data ?? "{}")!;
        data["basePrice"] = value;
        if (source is null)
        {
            source = new SourceRecord { Dataset = "client/types", Key = typeId.ToString() };
            catalog.Add(source);
        }
        source.Data = data.ToJsonString();
        var station = catalog.Set<Station>().Single(s => s.Id == db.Store.FindPilot(1000001)!.StationId);
        var operation = catalog
            .Set<SourceRecord>()
            .AsTracking()
            .SingleOrDefault(record => record.Dataset == "client/stationoperations" && record.Key == station.OperationId.ToString());
        if (operation is null)
        {
            operation = new SourceRecord { Dataset = "client/stationoperations", Key = station.OperationId.ToString() };
            catalog.Add(operation);
        }
        operation.Data = "{\"services\":[21]}";
        catalog.SaveChanges();
    }

    internal static void Fund(TestWorld db, long cents = 1_000_000)
    {
        using WorldDbContext world = new(db.Path);
        var wallet = world.Set<PilotWallet>().Find(90000001L) ?? world.Add(new PilotWallet { PilotId = 90000001 }).Entity;
        wallet.Cents = cents;
        world.SaveChanges();
    }
}

public sealed class InsuranceTests
{
    private const long PilotId = 90000001;
    private const long ShipId = 1000000000001;
    private const int StationId = 60003760;

    [Theory]
    [InlineData(InsuranceLevel.Basic, 5000, 50000)]
    [InlineData(InsuranceLevel.Standard, 10000, 60000)]
    [InlineData(InsuranceLevel.Bronze, 15000, 70000)]
    [InlineData(InsuranceLevel.Silver, 20000, 80000)]
    [InlineData(InsuranceLevel.Gold, 25000, 90000)]
    [InlineData(InsuranceLevel.Platinum, 30000, 100000)]
    public void NativePackagesHaveCorrectPremiumAndPayoutAndRemainDistinctAtMinimumValue(InsuranceLevel level, long premium, long payout)
    {
        Assert.Equal(premium, InsuranceRules.Premium(100000, level));
        Assert.Equal(payout, InsuranceRules.Payout(100000, level));
        Assert.Equal(level, InsuranceRules.LevelForPremium(100000, premium));
        Assert.Equal(40000, InsuranceRules.Payout(100000, InsuranceLevel.Uninsured));
        Assert.Equal(6, InsuranceRules.PaidLevels.Select(package => InsuranceRules.Premium(100, package)).Distinct().Count());
        Assert.Throws<InvalidDataException>(() => InsuranceRules.LevelForPremium(100000, premium + 1));
        Assert.Throws<InvalidDataException>(() => InsuranceRules.PaymentCents(double.NaN));
        Assert.Throws<InvalidDataException>(() => InsuranceRules.PaymentCents(double.PositiveInfinity));
        Assert.Equal(30000, InsuranceRules.PaymentCents(300.00000000000006));
    }

    [Theory]
    [InlineData(587, 25, 400000)]
    [InlineData(32880, 25, 300000)]
    [InlineData(24690, 27, 153900000)]
    [InlineData(601, 237, 0)]
    [InlineData(670, 29, 0)]
    public void QuotesUsePinnedCatalogValuesAcrossHullFamiliesRatherThanTestingMarketPrices(int typeId, int groupId, double price)
    {
        using TestWorld db = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            if (catalog.Set<ItemGroup>().Find(groupId) is null)
            {
                catalog.Add(
                    new ItemGroup
                    {
                        Id = groupId,
                        Name = "Quote fixture hull group",
                        CategoryId = 6,
                    }
                );
            }
            if (catalog.Set<ItemType>().Find(typeId) is null)
            {
                catalog.Add(
                    new ItemType
                    {
                        Id = typeId,
                        Name = "Quote fixture hull",
                        GroupId = groupId,
                    }
                );
            }
            catalog.SaveChanges();
        }
        InsuranceTestData.Prepare(db, typeId, price);
        var insurance = db.Services.GetRequiredService<IPilotInsurance>();
        Assert.Equal((long)(price * 100), insurance.Price(typeId));
        var batch = insurance.Prices([typeId, typeId, 3638, 123]);
        Assert.Equal(3, batch.Count);
        Assert.Equal(insurance.Price(typeId), batch[typeId]);
        Assert.Equal(0, batch[3638]);
        Assert.Equal(0, batch[123]);
    }

    [Fact]
    public void PurchaseConfirmationReplacementCancellationAndExpiryPreserveLedgerAcrossRestart()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        InsuranceTestData.Prepare(db);
        InsuranceTestData.Fund(db);
        var insurance = db.Services.GetRequiredService<IPilotInsurance>();
        var first = insurance.Insure(PilotId, StationId, ShipId, 5000, false);
        Assert.Equal(995000, first.BalanceCents);
        Assert.Equal(InsuranceLevel.Basic, first.Policy.Level);
        Assert.Equal(clock.Now + TimeSpan.FromDays(84).Ticks, first.Policy.EndTime);
        var duplicate = Assert.Throws<InsuranceContractExists>(() => insurance.Insure(PilotId, StationId, ShipId, 30000, false));
        Assert.Equal(PilotId, duplicate.OwnerId);
        Assert.Equal(995000, db.Services.GetRequiredService<IPilotWalletReader>().Balance(PilotId));
        Assert.Single(db.Services.GetRequiredService<IPilotWalletReader>().Journal(PilotId).Insurance);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path), clock);
        var persistent = reopened.GetRequiredService<IPilotInsurance>();
        Assert.Equal(first.Policy, Assert.Single(persistent.Contracts(PilotId)));
        Assert.Empty(persistent.Contracts(90000002));
        Assert.Null(persistent.Contract(90000002, ShipId));
        clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
        var replacement = persistent.Insure(PilotId, StationId, ShipId, 30000, true);
        Assert.NotEqual(first.Policy.Id, replacement.Policy.Id);
        Assert.Equal(965000, replacement.BalanceCents);
        Assert.Equal(InsuranceLevel.Platinum, replacement.Policy.Level);
        Assert.Equal(clock.Now + TimeSpan.FromDays(84).Ticks, replacement.Policy.EndTime);
        persistent.Cancel(PilotId, StationId, ShipId);
        Assert.Empty(persistent.Contracts(PilotId));
        Assert.Equal(965000, reopened.GetRequiredService<IPilotWalletReader>().Balance(PilotId));
        var renewed = persistent.Insure(PilotId, StationId, ShipId, 10000, false);
        clock.AdvanceWallTime(TimeSpan.FromDays(84) - TimeSpan.FromTicks(1));
        Assert.Equal(renewed.Policy, persistent.Contract(PilotId, ShipId));
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Empty(persistent.Contracts(PilotId));
        var afterExpiry = persistent.Insure(PilotId, StationId, ShipId, 15000, false);
        Assert.Equal(InsuranceLevel.Bronze, afterExpiry.Policy.Level);
        var journal = reopened.GetRequiredService<IPilotWalletReader>().Journal(PilotId).Insurance;
        Assert.Equal(new long[] { -5000, -30000, -10000, -15000 }, journal.Select(entry => entry.AmountCents));
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(InsurancePolicyStatus.Replaced, saved.Set<SavedInsurancePolicy>().Find(first.Policy.Id)!.Status);
        Assert.Equal(InsurancePolicyStatus.Cancelled, saved.Set<SavedInsurancePolicy>().Find(replacement.Policy.Id)!.Status);
        Assert.Equal(InsurancePolicyStatus.Expired, saved.Set<SavedInsurancePolicy>().Find(renewed.Policy.Id)!.Status);
    }

    [Fact]
    public void OwnershipLocationServiceAndPaymentRejectionsLeaveWalletAndPoliciesUnchanged()
    {
        using TestWorld db = new();
        InsuranceTestData.Prepare(db);
        InsuranceTestData.Fund(db);
        var insurance = db.Services.GetRequiredService<IPilotInsurance>();
        Assert.Throws<InvalidDataException>(() => insurance.Insure(PilotId, StationId, 1000000000002, 5000, false));
        Assert.Throws<InvalidDataException>(() => insurance.Insure(PilotId, StationId + 1, ShipId, 5000, false));
        Assert.Throws<InvalidDataException>(() => insurance.Insure(PilotId, StationId, 123, 5000, false));
        Assert.Throws<InvalidDataException>(() => insurance.Insure(PilotId, StationId, ShipId, 5001, false));
        Assert.Throws<InvalidDataException>(() => insurance.Cancel(PilotId, StationId, 1000000000002));
        db.Store.SetPosition(ShipId, new(1_000_000, 0, 0));
        Assert.Throws<InvalidDataException>(() => insurance.Insure(PilotId, StationId, ShipId, 5000, false));
        db.Store.SetPosition(ShipId, null);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog
                .Set<SourceRecord>()
                .AsTracking()
                .Single(record => record.Dataset == "client/stationoperations" && record.Key == "14")
                .Data = "{\"services\":[]}";
            catalog.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => insurance.Insure(PilotId, StationId, ShipId, 5000, false));
        Assert.Empty(insurance.Contracts(PilotId));
        Assert.Empty(db.Services.GetRequiredService<IPilotWalletReader>().Journal(PilotId).Insurance);
        Assert.Equal(1_000_000, db.Services.GetRequiredService<IPilotWalletReader>().Balance(PilotId));
        InsuranceTestData.Prepare(db);
        InsuranceTestData.Fund(db, 4999);
        Assert.Throws<InvalidDataException>(() => insurance.Insure(PilotId, StationId, ShipId, 5000, false));
        Assert.Equal(4999, db.Services.GetRequiredService<IPilotWalletReader>().Balance(PilotId));
        Assert.Empty(insurance.Contracts(PilotId));
        Assert.Empty(insurance.Contracts(90000002));
    }

    [Fact]
    public async Task ConcurrentPolicyPurchaseChargesOnceAndLeavesOneActiveContract()
    {
        using TestWorld db = new();
        InsuranceTestData.Prepare(db);
        InsuranceTestData.Fund(db);
        var insurance = db.Services.GetRequiredService<IPilotInsurance>();
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        try
                        {
                            insurance.Insure(PilotId, StationId, ShipId, 5000, false);
                            return true;
                        }
                        catch (InsuranceContractExists)
                        {
                            return false;
                        }
                    })
                )
        );
        Assert.Single(results, result => result);
        Assert.Single(insurance.Contracts(PilotId));
        Assert.Single(db.Services.GetRequiredService<IPilotWalletReader>().Journal(PilotId).Insurance);
        Assert.Equal(995000, db.Services.GetRequiredService<IPilotWalletReader>().Balance(PilotId));
    }

    [Fact]
    public void Version42MigrationPreservesPersonalDataAndCreatesEmptyInsuranceTables()
    {
        using TestWorld db = new();
        var bookmarks = db.Services.GetRequiredService<Eden.Gameplay.Bookmarks.IPilotBookmarks>();
        var folder = bookmarks.AddFolder(PilotId, "Keep", "");
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldInsuranceLedger; DROP TABLE WorldInsurancePolicy; PRAGMA user_version=42");
        }
        WorldInitialization.Open(db.Path);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        Assert.Empty(reopened.GetRequiredService<IPilotInsurance>().Contracts(PilotId));
        Assert.Empty(reopened.GetRequiredService<IPilotWalletReader>().Journal(PilotId).Insurance);
        Assert.Equal(folder, Assert.Single(reopened.GetRequiredService<Eden.Gameplay.Bookmarks.IPilotBookmarks>().Read(PilotId).Folders));
    }
}
