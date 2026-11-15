using Eden.Data.World.Corporations;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationLeadershipTests
{
    [Fact]
    public void NativeMemberAndShareholderDividendsConserveCentsAndSurviveReopen()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var wallet = world.Services.GetRequiredService<ICorporationWallet>();
        var balances = world.Services.GetRequiredService<IPilotWalletReader>();
        wallet.Transfer(90000001, new(false, 1000, corporation, 1000, 1000, "Dividend funding"));
        long ceoBefore = balances.Balance(90000001);
        long memberBefore = balances.Balance(90000002);
        using (CorporationTestClient ceo = new(world, 1000001))
        {
            Assert.Null(ceo.Reply("PayoutDividend", [0L, 1.01]));
            Assert.Equal(ceoBefore + 51, balances.Balance(90000001));
            Assert.Equal(memberBefore + 50, balances.Balance(90000002));
            Assert.Equal(899, wallet.Balance(90000001, 1000));
            world.Services.GetRequiredService<ICorporationShares>().Transfer(90000001, new(corporation, true, 90000002, 750));
            Assert.Null(ceo.Reply("PayoutDividend", [1L, 2d]));
            Assert.Equal(memberBefore + 200, balances.Balance(90000002));
            Assert.Equal(749, wallet.Balance(90000001, 1000));
            var journal = wallet.Journal(90000001, true, 1000).Where(row => row.EntryType == WalletEntryType.CorporationDividendPayment);
            Assert.Equal(new long[] { -200, -101, 50 }, journal.Select(row => row.AmountCents).Order().ToArray());
        }
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), liveWorld: true);
        Assert.Equal(749, reopened.GetRequiredService<ICorporationWallet>().Balance(90000001, 1000));
        Assert.Equal(memberBefore + 200, reopened.GetRequiredService<IPilotWalletReader>().Balance(90000002));
        using WorldDbContext saved = new(world.Path);
        var entries = saved.Set<SavedWalletLedger>().Where(row => row.EntryType == WalletEntryType.CorporationDividendPayment).ToArray();
        Assert.Equal(6, entries.Length);
        Assert.Equal(0, entries.Sum(row => row.AmountCents));
    }

    [Fact]
    public void RecipientOverflowAndInvalidRequestsRollBackDebitsCreditsAndJournals()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var wallet = world.Services.GetRequiredService<ICorporationWallet>();
        wallet.Transfer(90000001, new(false, 1000, corporation, 1000, 1000, "Funding"));
        using (WorldActionScope transaction = new(new(world.Path), TransactionParticipants.None))
        {
            var recipient =
                transaction.World.Set<PilotWallet>().Find(90000002L)
                ?? transaction.World.Add(new PilotWallet { PilotId = 90000002 }).Entity;
            recipient.Cents = long.MaxValue;
            transaction.Commit();
        }
        long before = world.Services.GetRequiredService<IPilotWalletReader>().Balance(90000001);
        using CorporationTestClient ceo = new(world, 1000001);
        using CorporationTestClient member = new(world, 1000002);
        foreach (object? amount in new object?[] { -1d, 0d, 0.001, double.NaN, double.PositiveInfinity, long.MaxValue, "1", 11d, 1d })
        {
            Assert.IsType<PilotCallResult.Failure>(ceo.Call("PayoutDividend", [0L, amount]));
        }
        Assert.IsType<PilotCallResult.Failure>(ceo.Call("PayoutDividend", [2L, 1d]));
        Assert.IsType<PilotCallResult.Failure>(ceo.Call("PayoutDividend", [1L, 1d], new() { ["corporationID"] = corporation }));
        Assign(world, CorporationRole.Director);
        Assert.IsType<PilotCallResult.Failure>(member.Call("PayoutDividend", [1L, 1d]));
        Assert.Equal(1000, wallet.Balance(90000001, 1000));
        Assert.Equal(before, world.Services.GetRequiredService<IPilotWalletReader>().Balance(90000001));
        Assert.Equal(long.MaxValue, world.Services.GetRequiredService<IPilotWalletReader>().Balance(90000002));
        using WorldDbContext saved = new(world.Path);
        Assert.Empty(saved.Set<SavedWalletLedger>().Where(row => row.EntryType == WalletEntryType.CorporationDividendPayment));
    }

    [Fact]
    public void DividendAllocationsHandleLargeWeightsAndDeterministicRemainders()
    {
        Assert.Equal(new[] { new CorporationDividendPayment(1, 1) }, CorporationDividendRules.Allocate(1, [new(2, 1), new(1, 1)]));
        var payments = CorporationDividendRules.Allocate(long.MaxValue, [new(1, long.MaxValue), new(2, long.MaxValue)]);
        Assert.Equal(long.MaxValue, payments.Sum(row => row.Cents));
        Assert.Equal(payments[1].Cents + 1, payments[0].Cents);
        Assert.Throws<InvalidDataException>(() => CorporationDividendRules.Allocate(0, [new(1, 1)]));
        Assert.Throws<InvalidDataException>(() => CorporationDividendRules.Allocate(1, []));
        Assert.Throws<InvalidDataException>(() => CorporationDividendRules.Allocate(1, [new(1, 0)]));
        Assert.Throws<InvalidDataException>(() => CorporationDividendRules.Allocate(1, [new(1, 1), new(1, 2)]));
    }

    [Fact]
    public void NativeSuccessionRestoresAssignedRolesRefreshesBothSessionsAndPersists()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        InjectManagement(world);
        Assign(world, CorporationRole.Accountant);
        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            Assert.Equal(new object?[] { 90000002L }, Assert.IsType<List<object?>>(ceo.Reply("GetNumberOfPotentialCEOs", [])));
            Assert.Equal(new object?[] { 90000002L }, Assert.IsType<List<object?>>(ceo.Reply("ResignFromCEO", [null])));
            Assert.Equal(90000001, world.Services.GetRequiredService<IPlayerCorporations>().Read(corporation)!.CeoId);
            Assert.Null(ceo.Reply("ResignFromCEO", [90000002L]));
            ceo.Advance();
            member.Advance();
            Assert.False(ceo.Context.Pilot.Identity.CorporationAccess!.Roles.HasAuthority);
            Assert.NotNull(ceo.Context.Pilot.Identity.CorporationAccess!.RolesRemovedAt);
            Assert.True(member.Context.Pilot.Identity.CorporationAccess!.Roles.Has(CorporationRole.Director));
            var changed = Assert.Single(ceo.Context.Notifications.OfType<CorporationDetailsChanged>());
            var changes = Assert.IsType<Dictionary<string, object?>>(changed.Arguments[1]);
            Assert.Equal(new object?[] { 90000001L, 90000002L }, changes["ceoID"]);
            Assert.False(world.Services.GetRequiredService<ICorporationMembership>().CanRemove(90000001, 90000001).Allowed);
            Assert.IsType<PilotCallResult.Failure>(ceo.Call("ResignFromCEO", [90000001L]));
            Assert.Null(member.Reply("ResignFromCEO", [90000001L]));
            member.Advance();
            Assert.Equal((long)CorporationRole.Accountant, member.Context.Pilot.Identity.CorporationAccess!.Roles.Roles);
        }
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), liveWorld: true);
        Assert.Equal(90000001, reopened.GetRequiredService<IPlayerCorporations>().Read(corporation)!.CeoId);
        Assert.Equal(
            (long)CorporationRole.Accountant,
            reopened.GetRequiredService<IPlayerCorporations>().Identity(90000002).Access!.Roles.Roles
        );
    }

    [Fact]
    public void SuccessorChecksUseCurrentMembershipInjectedSkillAndRoleAcceptance()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(world);
        var leadership = world.Services.GetRequiredService<ICorporationLeadership>();
        Assert.Empty(leadership.Successors(90000001));
        Assert.Throws<InvalidDataException>(() => leadership.Transfer(90000001, 90000002));
        InjectManagement(world);
        Assert.Equal(new long[] { 90000002 }, leadership.Successors(90000001));
        world
            .Services.GetRequiredService<ICorporationRoles>()
            .UpdateMembers(
                90000002,
                [new(90000002, null, null, null, new(null, null, null, null, null, null, null, null), null, null, true)]
            );
        Assert.Empty(leadership.Successors(90000001));
        Assert.Throws<InvalidDataException>(() => leadership.Transfer(90000001, 90000002));
        Assert.Throws<InvalidDataException>(() => leadership.Transfer(90000001, 90000001));
        Assert.Throws<InvalidDataException>(() => leadership.Transfer(90000002, 90000001));
        Assert.Equal(90000001, world.Services.GetRequiredService<IPlayerCorporations>().Read(corporation)!.CeoId);
    }

    private static void Assign(TestWorld world, CorporationRole role) =>
        world
            .Services.GetRequiredService<ICorporationRoles>()
            .UpdateMembers(90000001, [new(90000002, null, null, null, new((long)role, 0, 0, 0, 0, 0, 0, 0), null, 0)]);

    private static void InjectManagement(TestWorld world)
    {
        using WorldActionScope transaction = new(new(world.Path), TransactionParticipants.None);
        transaction.World.Add(new PilotSkill { PilotId = 90000002, TypeId = CorporationRules.ManagementSkill });
        transaction.Commit();
    }
}
