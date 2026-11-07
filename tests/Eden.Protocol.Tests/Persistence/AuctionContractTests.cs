using Eden.Data.Combat;
using Eden.Data.Contracts;
using Eden.Data.Insurance;
using Eden.Data.Inventory;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Server;
using Eden.World.Combat;
using Eden.World.Contracts;
using Eden.World.Insurance;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

internal static class AuctionTestData
{
    internal const long Issuer = ContractAssetTestData.Pilot;
    internal const long Bidder = ContractAssetTestData.Other;
    internal const long Funds = 1_000_000_000;
    internal const long Starting = 100_000_000;

    internal static ContractDraft Draft(long item, long? assignee = null, long buyout = 0) =>
        new(
            assignee,
            ContractAssetTestData.Station,
            1440,
            Starting,
            0,
            "Auction",
            "Mining equipment",
            [new(item, 7)],
            [],
            ContractType.Auction,
            Auction: new(buyout)
        );

    internal sealed class Fixture : IDisposable
    {
        internal TestClock Clock { get; } = CalendarTestData.Clock();
        internal TestWorld Db { get; }
        internal IPilotContracts Contracts { get; }
        internal long Item { get; }

        internal Fixture()
        {
            Db = new(clock: Clock);
            ContractTests.Fund(Db, Issuer, Funds);
            ContractTests.Fund(Db, Bidder, Funds);
            Item = ContractAssetTestData.Stack(Db, 7);
            Contracts = Db.Services.GetRequiredService<IPilotContracts>();
        }

        internal long Create(long? assignee = null, long buyout = 0) => Contracts.Create(Issuer, Draft(Item, assignee, buyout));

        internal long Balance(long pilot) => Db.Services.GetRequiredService<IPilotWalletReader>().Balance(pilot);

        internal void End(long id) => Clock.AdvanceWallTime(TimeSpan.FromTicks(Contracts.Details(Issuer, id).Contract.Expires - Clock.Now));

        public void Dispose() => Db.Dispose();
    }
}

public sealed class AuctionContractTests
{
    [Fact]
    public void AuctionSearchFiltersAndSortsCurrentBidsAndCanExcludeOffersWithoutBuyout()
    {
        using AuctionTestData.Fixture f = new();
        long noBuyout = f.Create();
        long otherItem = ContractAssetTestData.Stack(f.Db, 7, owner: AuctionTestData.Bidder);
        long withBuyout = f.Contracts.Create(AuctionTestData.Bidder, AuctionTestData.Draft(otherItem, buyout: 500_000_000));
        Assert.Equal(
            withBuyout,
            Assert.Single(f.Contracts.Search(AuctionTestData.Bidder, new(Type: 2, ExcludeNoBuyout: true)).Contracts).Contract.Id
        );
        f.Contracts.Bid(AuctionTestData.Bidder, noBuyout, 200_000_000);
        var result = f.Contracts.Search(AuctionTestData.Bidder, new(Type: 2, MinPrice: 200_000_000, MaxPrice: 200_000_000));
        Assert.Equal(noBuyout, Assert.Single(result.Contracts).Contract.Id);
        Assert.Equal(
            new[] { withBuyout, noBuyout },
            f.Contracts.Search(AuctionTestData.Bidder, new(Type: 2, Sort: ContractSort.Price, Descending: false))
                .Contracts.Select(c => c.Contract.Id)
        );
    }

    [Fact]
    public void SalesTaxUsesTheFinalWinningAmountRatherThanTheQuotedStartingAmount()
    {
        using AuctionTestData.Fixture f = new();
        ContractTests.Fund(f.Db, AuctionTestData.Bidder, 3_000_000_000);
        long id = f.Create();
        Assert.Equal(1_000_000, f.Contracts.Details(AuctionTestData.Issuer, id).Contract.Fees.SalesTaxCents);
        f.Contracts.Bid(AuctionTestData.Bidder, id, 2_000_000_000);
        f.End(id);
        f.Contracts.FinishAuction(AuctionTestData.Issuer, id, true);
        Assert.Equal(20_000_000, f.Contracts.Details(AuctionTestData.Issuer, id).Contract.Fees.SalesTaxCents);
        Assert.Equal(AuctionTestData.Funds - 1_000_000 + 2_000_000_000 - 20_000_000, f.Balance(AuctionTestData.Issuer));
    }

    [Fact]
    public void PrivateAuctionsRejectForeignBiddersAndCannotBeRejectedOnceFunded()
    {
        using AuctionTestData.Fixture f = new();
        long other = Assert.Single(CalendarTestData.AddPilots(f.Db, 1));
        ContractTests.Fund(f.Db, other, AuctionTestData.Funds);
        long id = f.Create(AuctionTestData.Bidder);
        Assert.Throws<InvalidDataException>(() => f.Contracts.Details(other, id));
        Assert.Throws<InvalidDataException>(() => f.Contracts.Bid(other, id, AuctionTestData.Starting));
        Assert.Equal(AuctionTestData.Funds, f.Balance(other));
        f.Contracts.Bid(AuctionTestData.Bidder, id, AuctionTestData.Starting);
        Assert.Throws<InvalidDataException>(() => f.Contracts.Reject(AuctionTestData.Bidder, id));
        Assert.Throws<InvalidDataException>(() => f.Contracts.FinishAuction(AuctionTestData.Bidder, id, true));
        f.End(id);
        Assert.Throws<InvalidDataException>(() => f.Contracts.FinishAuction(other, id, false));
        Assert.Equal(ContractStatus.Outstanding, f.Contracts.Details(AuctionTestData.Issuer, id).Contract.Status);
    }

    [Fact]
    public void AVersion51CourierWorldUpgradesWithoutChangingItsPackageWalletsOrGoods()
    {
        using TestWorld db = new();
        CourierTestData.Prepare(db);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long id = contracts.Create(CourierTestData.Issuer, CourierTestData.Draft(ContractAssetTestData.Stack(db)));
        var before = contracts.Accept(CourierTestData.Hauler, id);
        long escrow = contracts.Escrow(CourierTestData.Hauler).IskCents;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldContractBid;
                ALTER TABLE WorldContract DROP COLUMN BuyoutCents;
                ALTER TABLE WorldContract DROP COLUMN CurrentBidCents;
                ALTER TABLE WorldContract DROP COLUMN AuctionAccountingLevel;
                ALTER TABLE WorldContract DROP COLUMN IssuerCollected;
                ALTER TABLE WorldContract DROP COLUMN WinnerCollected;
                PRAGMA user_version=51;
                """
            );
        }
        WorldInitialization.Open(db.Path);
        Assert.Equal(before, contracts.Details(CourierTestData.Hauler, id).Contract);
        Assert.Equal(escrow, contracts.Escrow(CourierTestData.Hauler).IskCents);
        Assert.Equal(35, contracts.Package(CourierTestData.Hauler, before.Courier!.PackageId!.Value)!.Item.Volume);
        using WorldDbContext verify = new(db.Path);
        Assert.Empty(verify.Set<SavedContractBid>());
        Assert.Single(verify.Set<SavedContractEscrow>());
        Assert.Equal(WorldMigration.CurrentVersion, verify.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FittedHullDeliveryAndFailedInventoryFeedWritesPreserveTheWholeOwnershipGraph(bool buyout)
    {
        using AuctionTestData.Fixture f = new();
        long hull = ContractAssetTestData.Hull(f.Db);
        long child = ContractAssetTestData.Hull(f.Db, hull);
        long module = ContractAssetTestData.Stack(f.Db, 1, child, flag: (int)InventoryFlag.HighSlot0);
        long charges = ContractAssetTestData.Stack(f.Db, 40, module, flag: (int)InventoryFlag.Cargo);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(module)!.Online = true;
            world.Add(new ShipHealthState { ShipId = child, StructureDamage = 3 });
            world.SaveChanges();
        }
        InsuranceTestData.Prepare(f.Db);
        f.Db.Services.GetRequiredService<IPilotInsurance>()
            .Insure(AuctionTestData.Issuer, ContractAssetTestData.Station, hull, 5000, false);
        long id = f.Contracts.Create(
            AuctionTestData.Issuer,
            AuctionTestData.Draft(hull, buyout: buyout ? 200_000_000 : 0) with
            {
                Items = [new(hull, 1)],
            }
        );
        using var sql = new SqliteConnection($"Data Source={f.Db.Path};Pooling=false");
        sql.Open();
        using var command = sql.CreateCommand();
        command.CommandText =
            "CREATE TRIGGER fail_delivery BEFORE INSERT ON WorldContractChange WHEN NEW.PilotId=90000002 "
            + "BEGIN SELECT RAISE(ABORT,'delivery failed'); END";
        command.ExecuteNonQuery();
        if (buyout)
        {
            Assert.Throws<DbUpdateException>(() => f.Contracts.Bid(AuctionTestData.Bidder, id, 200_000_000));
            Assert.Equal(AuctionTestData.Funds, f.Balance(AuctionTestData.Bidder));
            Assert.Empty(f.Contracts.Details(AuctionTestData.Issuer, id).Bids!);
        }
        else
        {
            command.CommandText = "DROP TRIGGER fail_delivery";
            command.ExecuteNonQuery();
            f.Contracts.Bid(AuctionTestData.Bidder, id, 200_000_000);
            f.End(id);
            command.CommandText =
                "CREATE TRIGGER fail_delivery BEFORE INSERT ON WorldContractChange WHEN NEW.PilotId=90000002 "
                + "BEGIN SELECT RAISE(ABORT,'delivery failed'); END";
            command.ExecuteNonQuery();
            Assert.Throws<DbUpdateException>(() => f.Contracts.FinishAuction(AuctionTestData.Bidder, id, false));
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(AuctionTestData.Issuer, world.Ships.IgnoreQueryFilters().Single(s => s.Id == hull).OwnerId);
            Assert.Equal(AuctionTestData.Issuer, world.Ships.IgnoreQueryFilters().Single(s => s.Id == child).OwnerId);
            Assert.Equal(InsurancePolicyStatus.Active, world.Set<SavedInsurancePolicy>().Single().Status);
            Assert.Equal(4, world.Set<SavedContractEscrow>().Count());
        }
        command.CommandText = "DROP TRIGGER fail_delivery";
        command.ExecuteNonQuery();
        if (buyout)
        {
            f.Contracts.Bid(AuctionTestData.Bidder, id, 200_000_000);
        }
        else
        {
            f.Contracts.FinishAuction(AuctionTestData.Bidder, id, false);
        }
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(AuctionTestData.Bidder, verify.Ships.Find(hull)!.OwnerId);
        Assert.Equal(AuctionTestData.Bidder, verify.Ships.Find(child)!.OwnerId);
        Assert.Equal(hull, verify.Ships.Find(child)!.StoredInId);
        Assert.Equal(module, verify.Set<OwnedItem>().Find(charges)!.LocationId);
        Assert.Equal(AuctionTestData.Bidder, verify.Set<OwnedItem>().Find(charges)!.OwnerId);
        Assert.True(verify.Set<OwnedItem>().Find(module)!.Online);
        Assert.Equal(3, verify.Set<ShipHealthState>().Find(child)!.StructureDamage);
        Assert.Equal(InsurancePolicyStatus.HullRemoved, verify.Set<SavedInsurancePolicy>().Single().Status);
    }

    [Fact]
    public async Task ConcurrentBuyoutsDeliverAndDebitExactlyOnce()
    {
        using AuctionTestData.Fixture f = new();
        long other = Assert.Single(CalendarTestData.AddPilots(f.Db, 1));
        ContractTests.Fund(f.Db, other, AuctionTestData.Funds);
        long id = f.Create(buyout: 200_000_000);
        Task<bool> Bid(long pilot) =>
            Task.Run(() =>
            {
                try
                {
                    f.Contracts.Bid(pilot, id, 200_000_000);
                    return true;
                }
                catch (InvalidDataException)
                {
                    return false;
                }
            });
        bool[] result = await Task.WhenAll(Bid(other), Bid(AuctionTestData.Bidder));
        Assert.Single(result, success => success);
        Assert.Equal(2 * AuctionTestData.Funds - 200_000_000, f.Balance(other) + f.Balance(AuctionTestData.Bidder));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Single(world.Set<SavedContractBid>());
        Assert.Empty(world.Set<SavedContractEscrow>());
        Assert.Equal(world.Set<SavedContract>().Single().AcceptorId, world.Set<OwnedItem>().Find(f.Item)!.OwnerId);
    }

    [Fact]
    public void BidsHoldMoneyTopUpTheLeaderAndRefundAReplacedLeaderAtomically()
    {
        using AuctionTestData.Fixture f = new();
        long other = Assert.Single(CalendarTestData.AddPilots(f.Db, 1));
        ContractTests.Fund(f.Db, other, AuctionTestData.Funds);
        long id = f.Create();
        long cursor = f.Contracts.Observe(AuctionTestData.Bidder);
        f.Contracts.Bid(AuctionTestData.Bidder, id, AuctionTestData.Starting);
        f.Contracts.Bid(AuctionTestData.Bidder, id, 110_000_000);
        Assert.Equal(AuctionTestData.Funds - 110_000_000, f.Balance(AuctionTestData.Bidder));
        Assert.Equal(110_000_000, f.Contracts.Escrow(AuctionTestData.Bidder).IskCents);
        f.Contracts.Bid(other, id, 120_000_000);
        Assert.Equal(AuctionTestData.Funds, f.Balance(AuctionTestData.Bidder));
        Assert.Equal(AuctionTestData.Funds - 120_000_000, f.Balance(other));
        Assert.Equal(0, f.Contracts.Escrow(AuctionTestData.Bidder).IskCents);
        Assert.Equal(120_000_000, f.Contracts.Escrow(other).IskCents);
        var bids = f.Contracts.Details(AuctionTestData.Issuer, id).Bids!;
        Assert.Equal(new long[] { 120_000_000, 110_000_000, AuctionTestData.Starting }, bids.Select(b => b.AmountCents));
        Assert.Equal(other, bids[0].BidderId);
        Assert.Equal(ContractChangeKind.AuctionOutbid, f.Contracts.Changes(AuctionTestData.Bidder, cursor).Items.Last().Kind);
        Assert.Single(f.Contracts.Bids(AuctionTestData.Bidder));
        Assert.Equal(1, f.Contracts.Page(AuctionTestData.Bidder).BiddingOn);
        Assert.Throws<InvalidDataException>(() => f.Contracts.Delete(AuctionTestData.Issuer, id));
        Assert.Throws<InvalidDataException>(() => f.Contracts.Accept(AuctionTestData.Bidder, id));
        Assert.Throws<InvalidDataException>(() => f.Contracts.Bid(AuctionTestData.Issuer, id, 130_000_000));
        Assert.Throws<InvalidDataException>(() => f.Contracts.Bid(AuctionTestData.Bidder, id, 129_990_000));
        ContractTests.Fund(f.Db, AuctionTestData.Bidder, 1);
        Assert.Throws<InvalidDataException>(() => f.Contracts.Bid(AuctionTestData.Bidder, id, 130_000_000));
        Assert.Equal(AuctionTestData.Funds - 120_000_000, f.Balance(other));
        Assert.Equal(3, f.Contracts.Details(AuctionTestData.Issuer, id).Bids!.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IndependentClaimsWorkInEitherOrderAcrossRestartAndPayTaxOnTheWinningPrice(bool issuerFirst, bool isPrivate)
    {
        using AuctionTestData.Fixture f = new();
        long id = f.Create(isPrivate ? AuctionTestData.Bidder : null);
        f.Contracts.Bid(AuctionTestData.Bidder, id, 200_000_000);
        Assert.Throws<InvalidDataException>(() => f.Contracts.FinishAuction(AuctionTestData.Issuer, id, true));
        Assert.Throws<InvalidDataException>(() => f.Contracts.FinishAuction(AuctionTestData.Bidder, id, false));
        f.End(id);
        Assert.Single(f.Contracts.Page(AuctionTestData.Issuer).Attention);
        Assert.Single(f.Contracts.Page(AuctionTestData.Bidder).Attention);
        long first = issuerFirst ? AuctionTestData.Issuer : AuctionTestData.Bidder;
        f.Contracts.FinishAuction(first, id, issuerFirst);
        Assert.Equal(
            issuerFirst ? ContractStatus.FinishedIssuer : ContractStatus.FinishedContractor,
            f.Contracts.Details(first, id).Contract.Status
        );
        Assert.Empty(f.Contracts.Page(first).Attention);
        Assert.Equal(issuerFirst ? 0 : 200_000_000, f.Contracts.Escrow(AuctionTestData.Bidder).IskCents);
        WorldInitialization.Open(f.Db.Path);
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock);
        var contracts = reopened.GetRequiredService<IPilotContracts>();
        long second = issuerFirst ? AuctionTestData.Bidder : AuctionTestData.Issuer;
        contracts.FinishAuction(second, id, !issuerFirst);
        contracts.FinishAuction(second, id, !issuerFirst);
        contracts.FinishAuction(first, id, issuerFirst);
        var result = contracts.Details(first, id).Contract;
        Assert.Equal(ContractStatus.Finished, result.Status);
        Assert.True(result.Auction!.IssuerCollected && result.Auction.WinnerCollected);
        Assert.Equal(isPrivate ? 0 : 2_000_000, result.Fees.SalesTaxCents);
        Assert.Equal(AuctionTestData.Funds - 1_000_000 + 200_000_000 - (isPrivate ? 0 : 2_000_000), f.Balance(AuctionTestData.Issuer));
        Assert.Equal(AuctionTestData.Funds - 200_000_000, f.Balance(AuctionTestData.Bidder));
        Assert.Equal(0, contracts.Escrow(AuctionTestData.Issuer).IskCents);
        Assert.Equal(0, contracts.Escrow(AuctionTestData.Bidder).IskCents);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(AuctionTestData.Bidder, world.Set<OwnedItem>().Find(f.Item)!.OwnerId);
        Assert.Equal(7, world.Set<OwnedItem>().Find(f.Item)!.Quantity);
        Assert.Equal(ContractAssetTestData.Station, world.Set<OwnedItem>().Find(f.Item)!.LocationId);
        Assert.Empty(world.Set<SavedContractEscrow>());
        Assert.Single(world.Set<SavedContractPayment>().Where(p => p.Type == WalletEntryType.ContractAuctionSold));
    }

    [Fact]
    public void BuyoutDeliversImmediatelyButLeavesOnlyTheIssuersCashClaim()
    {
        using AuctionTestData.Fixture f = new();
        long id = f.Create(buyout: 500_000_000);
        f.Contracts.Bid(AuctionTestData.Bidder, id, 500_000_000);
        var contract = f.Contracts.Details(AuctionTestData.Bidder, id).Contract;
        Assert.Equal(ContractStatus.FinishedContractor, contract.Status);
        Assert.Equal(f.Clock.Now, contract.Expires);
        Assert.True(contract.Auction!.WinnerCollected);
        Assert.False(contract.Auction.IssuerCollected);
        Assert.Empty(f.Contracts.Page(AuctionTestData.Bidder).Attention);
        Assert.Single(f.Contracts.Page(AuctionTestData.Issuer).Attention);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(AuctionTestData.Bidder, world.Set<OwnedItem>().Find(f.Item)!.OwnerId);
        }
        Assert.Throws<InvalidDataException>(() => f.Contracts.Bid(AuctionTestData.Bidder, id, 500_000_000));
        f.Contracts.FinishAuction(AuctionTestData.Bidder, id, false);
        f.Contracts.FinishAuction(AuctionTestData.Issuer, id, true);
        Assert.Equal(ContractStatus.Finished, f.Contracts.Details(AuctionTestData.Issuer, id).Contract.Status);
    }

    [Fact]
    public void LateBidsRepeatedlyExtendTheDeadlineAndExpiredBidsCannotAlterTheWinner()
    {
        using AuctionTestData.Fixture f = new();
        long id = f.Create();
        long amount = AuctionTestData.Starting;
        for (int cycle = 0; cycle < 3; cycle++)
        {
            var old = f.Contracts.Details(AuctionTestData.Issuer, id).Contract;
            f.Clock.AdvanceWallTime(TimeSpan.FromTicks(old.Expires - f.Clock.Now - 1));
            f.Contracts.Bid(AuctionTestData.Bidder, id, amount);
            Assert.Equal(
                f.Clock.Now + AuctionRules.ExtensionWindow.Ticks,
                f.Contracts.Details(AuctionTestData.Issuer, id).Contract.Expires
            );
            Assert.Throws<InvalidDataException>(() => f.Contracts.FinishAuction(AuctionTestData.Issuer, id, true));
            amount += 10_000_000;
        }
        f.End(id);
        long balance = f.Balance(AuctionTestData.Bidder);
        Assert.Throws<InvalidDataException>(() => f.Contracts.Bid(AuctionTestData.Bidder, id, amount));
        Assert.Equal(balance, f.Balance(AuctionTestData.Bidder));
        Assert.Equal(3, f.Contracts.Details(AuctionTestData.Issuer, id).Bids!.Count);
    }

    [Fact]
    public void UnbidAuctionsCanBeCancelledAndRefundedWithoutOpeningOrdinaryAcceptance()
    {
        using AuctionTestData.Fixture f = new();
        long id = f.Create();
        Assert.Throws<InvalidDataException>(() => f.Contracts.Accept(AuctionTestData.Bidder, id));
        f.Contracts.Delete(AuctionTestData.Issuer, id);
        f.Contracts.Delete(AuctionTestData.Issuer, id);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(AuctionTestData.Issuer, world.Set<OwnedItem>().Find(f.Item)!.OwnerId);
        Assert.Equal(AuctionTestData.Funds - 11_000_000, f.Balance(AuctionTestData.Issuer));
        Assert.Equal(ContractStatus.Cancelled, f.Contracts.Details(AuctionTestData.Issuer, id).Contract.Status);
    }

    [Fact]
    public void FailedLedgerWritesRollBackAReplacementBidAndAnIssuersClaim()
    {
        using AuctionTestData.Fixture f = new();
        long other = Assert.Single(CalendarTestData.AddPilots(f.Db, 1));
        ContractTests.Fund(f.Db, other, AuctionTestData.Funds);
        long id = f.Create();
        f.Contracts.Bid(AuctionTestData.Bidder, id, AuctionTestData.Starting);
        using var sql = new SqliteConnection($"Data Source={f.Db.Path};Pooling=false");
        sql.Open();
        using var command = sql.CreateCommand();
        command.CommandText =
            "CREATE TRIGGER fail_refund BEFORE INSERT ON WorldContractPayment WHEN NEW.Type=64 "
            + "BEGIN SELECT RAISE(ABORT,'refund failed'); END";
        command.ExecuteNonQuery();
        Assert.Throws<DbUpdateException>(() => f.Contracts.Bid(other, id, 110_000_000));
        Assert.Equal(AuctionTestData.Funds, f.Balance(other));
        Assert.Equal(AuctionTestData.Funds - AuctionTestData.Starting, f.Balance(AuctionTestData.Bidder));
        Assert.Single(f.Contracts.Details(AuctionTestData.Issuer, id).Bids!);
        command.CommandText =
            "DROP TRIGGER fail_refund; CREATE TRIGGER fail_sale BEFORE INSERT ON WorldContractPayment WHEN NEW.Type=67 "
            + "BEGIN SELECT RAISE(ABORT,'sale failed'); END";
        command.ExecuteNonQuery();
        f.End(id);
        Assert.Throws<DbUpdateException>(() => f.Contracts.FinishAuction(AuctionTestData.Issuer, id, true));
        Assert.Equal(ContractStatus.Outstanding, f.Contracts.Details(AuctionTestData.Issuer, id).Contract.Status);
        Assert.Equal(AuctionTestData.Funds - 11_000_000, f.Balance(AuctionTestData.Issuer));
        Assert.Equal(AuctionTestData.Starting, f.Contracts.Escrow(AuctionTestData.Bidder).IskCents);
    }

    [Fact]
    public void OnlyALosingOrCollectedBidderMayDismissItsOwnBidHistory()
    {
        using AuctionTestData.Fixture f = new();
        long other = Assert.Single(CalendarTestData.AddPilots(f.Db, 1));
        ContractTests.Fund(f.Db, other, AuctionTestData.Funds);
        long id = f.Create();
        f.Contracts.Bid(AuctionTestData.Bidder, id, AuctionTestData.Starting);
        Assert.Throws<InvalidDataException>(() => f.Contracts.ForgetAuction(AuctionTestData.Bidder, id));
        Assert.Throws<InvalidDataException>(() => f.Contracts.ForgetAuction(AuctionTestData.Issuer, id));
        f.Contracts.Bid(other, id, 110_000_000);
        f.Contracts.ForgetAuction(AuctionTestData.Bidder, id);
        Assert.Empty(f.Contracts.Bids(AuctionTestData.Bidder));
        Assert.Single(f.Contracts.Bids(other));
        Assert.Equal(0, f.Contracts.Page(AuctionTestData.Bidder).BiddingOn);
        Assert.Equal(2, f.Contracts.Details(other, id).Bids!.Count);
        WorldInitialization.Open(f.Db.Path);
        Assert.Empty(f.Contracts.Bids(AuctionTestData.Bidder));
    }
}
