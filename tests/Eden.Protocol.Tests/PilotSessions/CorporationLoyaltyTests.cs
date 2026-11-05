using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Gameplay.Redemption;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationLoyaltyTests
{
    private const int Issuer = 1000044;
    private const int EverMarks = CurrencyRewardRules.ParagonCorporation;

    private static void Seed(TestWorld world, long pilot = 90000001)
    {
        using (CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO NpcCorporation VALUES (1000419,'Paragon')");
        }
        using WorldDbContext saved = new(world.Path);
        saved.Add(
            new SavedLoyaltyBalance
            {
                PilotId = pilot,
                IssuerCorporationId = Issuer,
                Points = 1000,
            }
        );
        saved.Add(
            new SavedLoyaltyBalance
            {
                PilotId = pilot,
                IssuerCorporationId = EverMarks,
                Points = 50,
            }
        );
        saved.SaveChanges();
    }

    private static object? Reply(CorporationTestClient client, string method, object?[] args) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(client.CallService("LPSvc", method, args)).Reply));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeDonationBalancesNotificationsAndLedgerPersistAndConserveEachIssuer(bool liveWorld)
    {
        TestClock clock = new();
        using TestWorld world = new(liveWorld: liveWorld, clock: clock);
        PlayerCorporationTests.Prepare(world);
        PlayerCorporationTests.Prepare(world, 90000002);
        var corporations = world.Services.GetRequiredService<IPlayerCorporations>();
        int first = corporations.Create(90000001, PlayerCorporationTests.Details()).Corporation.Id;
        int second = corporations.Create(90000002, PlayerCorporationTests.Details("Second Corporation", "TWO")).Corporation.Id;
        Seed(world);
        var personal = world.Services.GetRequiredService<IPilotLoyaltyWallet>();
        Assert.Equal(1000, personal.Read(90000001).Single(b => b.IssuerCorporationId == Issuer).Points);
        using (CorporationTestClient donor = new(world, 1000001))
        using (CorporationTestClient recipient = new(world, 1000002))
        {
            var donorMonitor = donor.Services.GetRequiredService<LoyaltyWalletHandler>();
            var recipientMonitor = recipient.Services.GetRequiredService<LoyaltyWalletHandler>();
            donorMonitor.Enter(donor.Context);
            recipientMonitor.Enter(recipient.Context);
            Assert.Null(Reply(donor, "TransferLPFromMyWalletToOtherCorp", [(long)first, (long)Issuer, 600L]));
            Assert.Null(Reply(donor, "TransferLPFromMyCorpWalletToOtherCorp", [(long)second, (long)Issuer, 250L]));
            Assert.IsType<PilotCallResult.Failure>(
                donor.CallService("LPSvc", "TransferLPFromMyWalletToOtherCorp", [(long)first, (long)EverMarks, 20L])
            );
            clock.AdvanceWallTime(CorporationLoyaltyRules.EverMarkMembership);
            Assert.Null(Reply(donor, "TransferLPFromMyWalletToOtherCorp", [(long)first, (long)EverMarks, 20L]));
            Assert.IsType<PilotCallResult.Failure>(
                donor.CallService("LPSvc", "TransferLPFromMyWalletToOtherCorp", [(long)second, (long)EverMarks, 1L])
            );
            Assert.IsType<PilotCallResult.Failure>(
                donor.CallService("LPSvc", "TransferLPFromMyCorpWalletToOtherCorp", [(long)second, (long)EverMarks, 1L])
            );
            recipientMonitor.Advance(recipient.Context);
            Assert.Single(recipient.Context.Notifications.OfType<CorporationLoyaltyBalanceChanged>());
            recipientMonitor.Advance(recipient.Context);
            Assert.Single(recipient.Context.Notifications.OfType<CorporationLoyaltyBalanceChanged>());
            var balances = Assert.IsType<List<object?>>(Reply(donor, "GetAllMyCorporationWalletLPBalances", []));
            Assert.Equal(new object?[] { (long)Issuer, 350L }, BlueRead.Tuple(balances[0], 2));
            Assert.Equal(new object?[] { (long)EverMarks, 20L }, BlueRead.Tuple(balances[1], 2));
            Assert.Equal(2, donor.Context.Notifications.OfType<LoyaltyBalanceChanged>().Count());
            Assert.Equal(3, donor.Context.Notifications.OfType<CorporationLoyaltyBalanceChanged>().Count());
            var privateReply = donor.CallService("charMgr", "GetPrivateInfoOnCorpChange", [90000001L]);
            var fields = BlueRead.Fields(
                Assert
                    .IsType<BlueInstance>(
                        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(privateReply).Reply))
                    )
                    .State
            );
            Assert.Equal(corporations.Identity(90000001).JoinedAt, fields["corporationDateTime"]);
            Assert.IsType<PilotCallResult.Failure>(recipient.CallService("charMgr", "GetPrivateInfoOnCorpChange", [90000001L]));
            Assert.Equal(400, personal.Read(90000001).Single(b => b.IssuerCorporationId == Issuer).Points);
            Assert.Equal(30, personal.Read(90000001).Single(b => b.IssuerCorporationId == EverMarks).Points);
        }
        world.Services.Dispose();
        WorldInitialization.Initialize(new(world.Path), clock);
        using var reopened = ServerServices.Build(new(world.Path), clock);
        Assert.Equal(
            350,
            reopened.GetRequiredService<ICorporationLoyaltyWallet>().Read(90000001).Single(b => b.IssuerCorporationId == Issuer).Points
        );
        Assert.Equal(250, reopened.GetRequiredService<ICorporationLoyaltyWallet>().Read(90000002).Single().Points);
        using WorldDbContext check = new(world.Path);
        Assert.Equal(3, check.Set<SavedCorporationLoyaltyTransfer>().Count());
        foreach (int issuer in new[] { Issuer, EverMarks })
        {
            long total =
                check.Set<SavedLoyaltyBalance>().Where(b => b.IssuerCorporationId == issuer).Sum(b => b.Points)
                + check.Set<SavedCorporationLoyaltyBalance>().Where(b => b.IssuerCorporationId == issuer).Sum(b => b.Points);
            Assert.Equal(issuer == Issuer ? 1000 : 50, total);
        }
    }

    [Fact]
    public void ReadOnlyRolesAndForgedMalformedInsufficientAndOverflowTransfersCannotSpendPoints()
    {
        using TestWorld world = new();
        int first = CorporationAdministrationTests.CreateAndJoin(world);
        TestCatalog.AddSchool(new WorldDatabase(world.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        long other = world.Services.GetRequiredService<ICharacterCreation>().Create(1000001, new("Other CEO", 1, 1, 1, 64, [1], [2], 18));
        PlayerCorporationTests.Prepare(world, other);
        int second = world
            .Services.GetRequiredService<IPlayerCorporations>()
            .Create(other, PlayerCorporationTests.Details("Other Corporation", "OTHER"))
            .Corporation.Id;
        Seed(world);
        var wallet = world.Services.GetRequiredService<ICorporationLoyaltyWallet>();
        wallet.Transfer(90000001, false, first, Issuer, 600);
        Assert.Empty(wallet.Read(90000002));
        var roles = world.Services.GetRequiredService<ICorporationRoles>();
        void Assign(CorporationRole role) =>
            roles.UpdateMembers(90000001, [new(90000002, null, null, null, new((long)role, 0, 0, 0, 0, 0, 0, 0), null, 0)]);
        Assign(CorporationRole.JuniorAccountant);
        Assert.Equal(600, Assert.Single(wallet.Read(90000002)).Points);
        Assert.Throws<InvalidDataException>(() => wallet.Transfer(90000002, true, second, Issuer, 1));
        Assign(CorporationRole.Accountant);
        wallet.Transfer(90000002, true, second, Issuer, 1);
        Assert.Equal(599, Assert.Single(wallet.Read(90000002)).Points);
        foreach (long amount in new long[] { 0, -1, long.MaxValue })
        {
            Assert.Throws<InvalidDataException>(() => wallet.Transfer(90000001, false, first, Issuer, amount));
        }
        Assert.Throws<InvalidDataException>(() => wallet.Transfer(90000001, false, 999999, Issuer, 1));
        Assert.Throws<InvalidDataException>(() => wallet.Transfer(90000001, false, first, 999999, 1));
        using CorporationTestClient client = new(world, 1000001);
        Assert.IsType<PilotCallResult.Failure>(
            client.CallService("LPSvc", "TransferLPFromMyWalletToOtherCorp", [(long)first, (long)Issuer, 1.5])
        );
        Assert.IsType<PilotCallResult.Failure>(
            client.CallService(
                "LPSvc",
                "TransferLPFromMyWalletToOtherCorp",
                [(long)first, (long)Issuer, 1L],
                new() { ["fromID"] = 90000002L }
            )
        );
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Set<SavedCorporationLoyaltyBalance>().Find(first, Issuer)!.Points = long.MaxValue;
            saved.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => wallet.Transfer(90000001, false, first, Issuer, 1));
        Assert.Equal(
            400,
            world.Services.GetRequiredService<IPilotLoyaltyWallet>().Read(90000001).Single(b => b.IssuerCorporationId == Issuer).Points
        );
    }

    [Fact]
    public void EverMarkMembershipBoundaryAndStandaloneRecipientRestrictionsCannotBeBypassed()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock);
        int first = CorporationAdministrationTests.CreateAndJoin(world);
        Seed(world, 90000002);
        var wallet = world.Services.GetRequiredService<ICorporationLoyaltyWallet>();
        clock.AdvanceWallTime(CorporationLoyaltyRules.EverMarkMembership - TimeSpan.FromTicks(1));
        Assert.Throws<InvalidDataException>(() => wallet.Transfer(90000002, false, first, EverMarks, 1));
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        wallet.Transfer(90000002, false, first, EverMarks, 1);
        Assert.Equal(1, wallet.Read(90000001).Single().Points);
        Assert.Throws<InvalidDataException>(() => wallet.Transfer(90000001, true, first, EverMarks, 1));
        using WorldDbContext saved = new(world.Path);
        Assert.Single(saved.Set<SavedCorporationLoyaltyTransfer>());
        Assert.Equal(49, saved.Set<SavedLoyaltyBalance>().Find(90000002L, EverMarks)!.Points);
    }

    [Fact]
    public void FailedTransferLedgerWriteRollsBackBothPointBalances()
    {
        using TestWorld world = new();
        int id = CorporationAdministrationTests.CreateAndJoin(world);
        Seed(world);
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER RejectLoyaltyTransfer BEFORE INSERT ON WorldCorporationLoyaltyTransfer
                BEGIN SELECT RAISE(ABORT,'test LP journal failure'); END;
                """
            );
        }
        var wallet = world.Services.GetRequiredService<ICorporationLoyaltyWallet>();
        Assert.Throws<DbUpdateException>(() => wallet.Transfer(90000001, false, id, Issuer, 1));
        Assert.Empty(wallet.Read(90000001));
        Assert.Equal(
            1000,
            world.Services.GetRequiredService<IPilotLoyaltyWallet>().Read(90000001).Single(b => b.IssuerCorporationId == Issuer).Points
        );
        using WorldDbContext check = new(world.Path);
        Assert.Empty(check.Set<SavedCorporationLoyaltyTransfer>());
    }

    [Fact]
    public void Version76UpgradePreservesPersonalPointsAndStartsCorporationPointsEmpty()
    {
        using TestWorld world = new();
        CorporationAdministrationTests.CreateAndJoin(world);
        Seed(world);
        world.Services.Dispose();
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldCorporationLoyaltyWallet;
                DROP TABLE WorldCorporationLoyaltyTransfer;
                PRAGMA user_version=76;
                """
            );
        }
        WorldInitialization.Initialize(new(world.Path));
        using var reopened = ServerServices.Build(new(world.Path));
        Assert.Empty(reopened.GetRequiredService<ICorporationLoyaltyWallet>().Read(90000001));
        Assert.Equal(
            1000,
            reopened.GetRequiredService<IPilotLoyaltyWallet>().Read(90000001).Single(b => b.IssuerCorporationId == Issuer).Points
        );
        using WorldDbContext check = new(world.Path);
        Assert.Equal(WorldMigration.CurrentVersion, check.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }
}
