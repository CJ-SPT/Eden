using Eden.Data.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationShareTests
{
    [Fact]
    public void NativeBindFirstCallReturnsIndexableCompanyHoldingsAndShareholderRows()
    {
        using TestWorld world = new();
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        using CorporationTestClient client = new(world, 1000001);
        var result = client.CallService(
            "corpRegistry",
            "MachoBindObject",
            [(long)corporation, new object?[] { "GetSharesByShareholder", new object?[] { true } }]
        );
        var bound = Assert.IsType<BoundPilotReply>(Assert.IsType<PilotCallResult.Success>(result).Reply);
        Assert.Equal("corpRegistry", client.Context.ResolveReference(bound.Reference.Id));
        Assert.NotEmpty(BlueMarshal.Encode(bound));
        var fields = BlueRead.Fields(Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(bound.Reply))).State);
        var header = Assert.IsType<List<object?>>(fields["header"]).Select(BlueMarshal.Text).ToArray();
        Assert.Equal(new[] { "corporationID", "shareholderID", "shares", "shareholderCorporationID" }, header);
        var line = Assert.IsType<List<object?>>(Assert.Single(Assert.IsType<List<object?>>(fields["lines"])));
        Assert.Equal(new object?[] { (long)corporation, (long)corporation, CorporationRules.InitialShares, null }, line);
        var holders = Assert.IsType<BlueInstance>(client.Reply("GetShareholders", [(long)corporation]));
        Assert.Equal("util.IndexRowset", holders.Name);
        var indexed = BlueRead.Fields(holders.State);
        Assert.Equal("shareholderID", BlueMarshal.Text(indexed["idName"]));
        Assert.Equal(line, Assert.IsType<Dictionary<long, object?>>(indexed["items"])[corporation]);
        var personal = BlueRead.Fields(Assert.IsType<BlueInstance>(client.Reply("GetSharesByShareholder", [false])).State);
        Assert.Empty(Assert.IsType<List<object?>>(personal["lines"]));
        Assert.IsType<PilotCallResult.Failure>(
            client.CallService(
                "corpRegistry",
                "MachoBindObject",
                [(long)corporation + 1, new object?[] { "GetSharesByShareholder", new object?[] { true } }]
            )
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompanyAndPrivateTransfersConserveSharesAndSurviveReopenWithoutReissuing(bool liveWorld)
    {
        using TestWorld world = new(liveWorld: liveWorld);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var shares = world.Services.GetRequiredService<ICorporationShares>();
        long wallet = world.Services.GetRequiredService<IPilotWalletReader>().Balance(90000001);
        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            Assert.IsType<PilotCallResult.Success>(ceo.Call("MoveCompanyShares", [(long)corporation, 90000002L, 600L]));
            Assert.IsType<PilotCallResult.Success>(member.Call("MovePrivateShares", [(long)corporation, 90000001L, 125L]));
            Assert.IsType<PilotCallResult.Success>(ceo.Call("MovePrivateShares", [(long)corporation, (long)corporation, 125L]));
        }
        Assert.Equal(525, Assert.Single(shares.Holdings(90000001, true)).Shares);
        Assert.Equal(475, Assert.Single(shares.Holdings(90000002, false)).Shares);
        Assert.Empty(shares.Holdings(90000001, false));
        Assert.Equal(wallet, world.Services.GetRequiredService<IPilotWalletReader>().Balance(90000001));
        world.Services.Dispose();
        WorldInitialization.Initialize(new(world.Path));
        using var reopened = ServerServices.Build(new(world.Path));
        var persisted = reopened.GetRequiredService<ICorporationShares>();
        Assert.Equal(525, Assert.Single(persisted.Holdings(90000001, true)).Shares);
        Assert.Equal(475, Assert.Single(persisted.Holdings(90000002, false)).Shares);
        Assert.Equal(CorporationRules.InitialShares, persisted.Shareholders(90000001, corporation).Sum(h => h.Shares));
        using WorldDbContext check = new(world.Path);
        Assert.Equal(3, check.Set<SavedCorporationShareTransfer>().Count());
        Assert.Equal(CorporationRules.InitialShares, check.Set<SavedCorporationShareIssue>().Find(corporation)!.Shares);
    }

    [Fact]
    public void PersonalHoldingsRemainOwnedAndTransferableAfterLeavingTheIssuerCorporation()
    {
        using TestWorld world = new();
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var shares = world.Services.GetRequiredService<ICorporationShares>();
        shares.Transfer(90000001, new(corporation, true, 90000002, 100));
        world.Services.GetRequiredService<ICorporationMembership>().Remove(90000002, 90000002);
        using CorporationTestClient formerMember = new(world, 1000002);
        var rows = BlueRead.Fields(Assert.IsType<BlueInstance>(formerMember.Reply("GetSharesByShareholder", [false])).State);
        Assert.Single(Assert.IsType<List<object?>>(rows["lines"]));
        Assert.IsType<PilotCallResult.Success>(formerMember.Call("MovePrivateShares", [(long)corporation, 90000001L, 100L]));
        Assert.Empty(shares.Holdings(90000002, false));
        Assert.Equal(100, Assert.Single(shares.Holdings(90000001, false)).Shares);
        Assert.IsType<PilotCallResult.Failure>(formerMember.Call("MoveCompanyShares", [(long)corporation, 90000002L, 1L]));
    }

    [Fact]
    public void CompanyMayOwnAnotherIssuersSharesAndOnlyItsCurrentDirectorsMayMoveThem()
    {
        using TestWorld world = new();
        PlayerCorporationTests.Prepare(world);
        PlayerCorporationTests.Prepare(world, 90000002);
        var corporations = world.Services.GetRequiredService<IPlayerCorporations>();
        int first = corporations.Create(90000001, PlayerCorporationTests.Details()).Corporation.Id;
        int second = corporations.Create(90000002, PlayerCorporationTests.Details("Second Corporation", "SEC")).Corporation.Id;
        var shares = world.Services.GetRequiredService<ICorporationShares>();
        shares.Transfer(90000001, new(first, true, second, 200));
        Assert.Equal(2, shares.Holdings(90000002, true).Count);
        shares.Transfer(90000002, new(first, true, 90000001, 50));
        Assert.Equal(150, shares.Holdings(90000002, true).Single(h => h.CorporationId == first).Shares);
        Assert.Equal(50, Assert.Single(shares.Holdings(90000001, false)).Shares);
        Assert.Equal(1000, shares.Shareholders(90000001, first).Sum(h => h.Shares));
        Assert.Throws<InvalidDataException>(() => shares.Shareholders(90000001, second));
    }

    [Fact]
    public void ReadsAndWritesUseCurrentPermissionsAndRejectedTransfersLeaveEveryBalanceUnchanged()
    {
        using TestWorld world = new();
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        using CorporationTestClient member = new(world, 1000002);
        Assert.IsType<PilotCallResult.Failure>(member.Call("GetSharesByShareholder", [true]));
        Assert.IsType<PilotCallResult.Failure>(member.Call("GetShareholders", [(long)corporation]));
        Assign(world, CorporationRole.JuniorAccountant);
        Assert.IsType<BlueInstance>(member.Reply("GetSharesByShareholder", [true]));
        Assert.IsType<PilotCallResult.Failure>(member.Call("MoveCompanyShares", [(long)corporation, 90000002L, 1L]));
        Assign(world, CorporationRole.Director);
        Assert.IsType<PilotCallResult.Success>(member.Call("MoveCompanyShares", [(long)corporation, 90000002L, 10L]));
        Assign(world, CorporationRole.NoRole);
        Assert.IsType<PilotCallResult.Failure>(member.Call("MoveCompanyShares", [(long)corporation, 90000002L, 1L]));
        var shares = world.Services.GetRequiredService<ICorporationShares>();
        Assert.Equal(990, Assert.Single(shares.Holdings(90000001, true)).Shares);
        Assert.Equal(10, Assert.Single(shares.Holdings(90000002, false)).Shares);
    }

    [Fact]
    public void ForgedInvalidAndExcessiveTransfersDoNotCreateHoldingsLedgerEntriesOrNotifications()
    {
        using TestWorld world = new();
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        using CorporationTestClient ceo = new(world, 1000001);
        object?[][] invalid =
        [
            [(long)corporation, 90000002L, 0L],
            [(long)corporation, 90000002L, -1L],
            [(long)corporation, 90000002L, long.MaxValue],
            [(long)corporation, 90000002L, 1.5],
            [(long)corporation, 1L, 1L],
            [(long)corporation, (long)corporation, 1L],
            [1L, 90000002L, 1L],
            [],
        ];
        foreach (var request in invalid)
        {
            Assert.IsType<PilotCallResult.Failure>(ceo.Call("MoveCompanyShares", request));
        }
        Assert.IsType<PilotCallResult.Failure>(ceo.Call("MovePrivateShares", [(long)corporation, 90000002L, 1L]));
        Assert.IsType<PilotCallResult.Failure>(ceo.Call("GetSharesByShareholder", [2L]));
        Assert.IsType<PilotCallResult.Failure>(
            ceo.Call("MoveCompanyShares", [(long)corporation, 90000002L, 1L], new() { ["fromOwner"] = 90000002L })
        );
        using WorldDbContext saved = new(world.Path);
        Assert.Equal(CorporationRules.InitialShares, Assert.Single(saved.Set<SavedCorporationShareHolding>()).Shares);
        Assert.Empty(saved.Set<SavedCorporationShareTransfer>());
        Assert.DoesNotContain(ceo.Context.Notifications, n => n.Name == "OnShareChange");
    }

    [Fact]
    public void FailedTransferLedgerRollsBackBothHoldingsAndPendingEvents()
    {
        using TestWorld world = new();
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        long head = world.Services.GetRequiredService<ICorporationMembership>().Head(90000001);
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER RejectShareLedger BEFORE INSERT ON WorldCorporationShareTransfer
                BEGIN SELECT RAISE(ABORT,'share transfer test failure'); END;
                """
            );
        }
        Assert.Throws<DbUpdateException>(() =>
            world.Services.GetRequiredService<ICorporationShares>().Transfer(90000001, new(corporation, true, 90000002, 50))
        );
        using WorldDbContext check = new(world.Path);
        Assert.Equal(1000, Assert.Single(check.Set<SavedCorporationShareHolding>()).Shares);
        Assert.Empty(check.Set<SavedCorporationShareTransfer>());
        Assert.Equal(head, world.Services.GetRequiredService<ICorporationMembership>().Head(90000001));
    }

    [Fact]
    public void OrderedInvalidationsEvictNativeCertificateCachesAndDoNotRepeatOrReachRevokedReaders()
    {
        using TestWorld world = new();
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        using CorporationTestClient ceo = new(world, 1000001);
        using CorporationTestClient member = new(world, 1000002);
        var shares = world.Services.GetRequiredService<ICorporationShares>();
        shares.Transfer(90000001, new(corporation, true, 90000002, 50));
        shares.Transfer(90000001, new(corporation, true, 90000002, 25));
        ceo.Advance();
        member.Advance();
        var changes = ceo.Context.Notifications.Where(n => n.Name == "OnShareChange").ToArray();
        Assert.Equal(2, changes.Length);
        Assert.Equal(new[] { 1000L, 950L }, changes.Select(n => (long)BlueRead.Tuple(BlueRead.Fields(n.Arguments[2])["shares"], 2)[0]!));
        foreach (var notification in changes)
        {
            var args = Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(notification.Arguments)));
            Assert.Equal((long)corporation, args[0]);
            // bco_base.GetAddRemoveFromChange recognizes this as a cache eviction.
            Assert.All(BlueRead.Fields(args[2]).Values, pair => Assert.Null(BlueRead.Tuple(pair, 2)[1]));
        }
        Assert.Equal(2, member.Context.Notifications.Count(n => n.Name == "OnShareChange"));
        Assert.Equal(75, Assert.Single(shares.Holdings(90000002, false)).Shares);
        ceo.Context.Notifications.Clear();
        member.Context.Notifications.Clear();
        ceo.Advance();
        member.Advance();
        Assert.DoesNotContain(ceo.Context.Notifications, n => n.Name == "OnShareChange");
        Assert.DoesNotContain(member.Context.Notifications, n => n.Name == "OnShareChange");
        Assign(world, CorporationRole.JuniorAccountant);
        shares.Transfer(90000001, new(corporation, true, 90000001, 1));
        Assign(world, CorporationRole.NoRole);
        member.Advance();
        Assert.DoesNotContain(member.Context.Notifications, n => n.Name == "OnShareChange");
    }

    [Fact]
    public void Version79BackfillsExistingTreasuryOnceAndRetainsTransfersOnFurtherStartup()
    {
        using TestWorld world = new();
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        world.Services.Dispose();
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldCorporationShareTransfer;
                DROP TABLE WorldCorporationShareHolding;
                DROP TABLE WorldCorporationShareIssue;
                ALTER TABLE WorldCorporationChange DROP COLUMN ShareJson;
                PRAGMA user_version=79;
                """
            );
        }
        WorldInitialization.Initialize(new(world.Path));
        using (var migrated = ServerServices.Build(new(world.Path)))
        {
            var shares = migrated.GetRequiredService<ICorporationShares>();
            Assert.Equal(1000, Assert.Single(shares.Holdings(90000001, true)).Shares);
            shares.Transfer(90000001, new(corporation, true, 90000002, 1000));
        }
        WorldInitialization.Initialize(new(world.Path));
        using var reopened = ServerServices.Build(new(world.Path));
        Assert.Empty(reopened.GetRequiredService<ICorporationShares>().Holdings(90000001, true));
        Assert.Equal(1000, Assert.Single(reopened.GetRequiredService<ICorporationShares>().Holdings(90000002, false)).Shares);
        using WorldDbContext check = new(world.Path);
        Assert.Equal(WorldMigration.CurrentVersion, check.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Equal(0, check.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").Single());
    }

    private static void Assign(TestWorld world, CorporationRole role) =>
        world
            .Services.GetRequiredService<ICorporationRoles>()
            .UpdateMembers(90000001, [new(90000002, null, null, null, new((long)role, 0, 0, 0, 0, 0, 0, 0), null, 0)]);

    [Theory]
    [InlineData(-1, 0, 1)]
    [InlineData(10, -1, 1)]
    [InlineData(10, 0, 0)]
    [InlineData(10, 0, -1)]
    [InlineData(10, 0, 11)]
    [InlineData(10, long.MaxValue, 1)]
    public void ShareRulesRejectInvalidBalancesQuantitiesAndOverflow(long source, long destination, long quantity)
    {
        Assert.Throws<InvalidDataException>(() => CorporationShareRules.Transfer(source, destination, quantity));
    }

    [Fact]
    public void ShareRulesConserveTheFullSourceAndAcceptTheExactDestinationLimit()
    {
        Assert.Equal(new(0, 1000), CorporationShareRules.Transfer(1000, 0, 1000));
        Assert.Equal(new(0, long.MaxValue), CorporationShareRules.Transfer(1, long.MaxValue - 1, 1));
    }
}
