using System.Text.Json;
using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Gameplay.Notifications;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Alliances;
using Eden.Protocol.Notifications;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Alliances.AllianceCreationTests;

namespace Eden.Protocol.Tests.Alliances;

public sealed class AllianceBillingTests
{
    internal static void Fund(TestWorld world, int corporation, long amount, int key = CorporationWalletRules.MasterKey, bool auto = false)
    {
        using var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None);
        if (key == CorporationWalletRules.MasterKey)
        {
            action.Set<SavedCorporation>().Find(corporation)!.BalanceCents = amount;
        }
        else
        {
            action.Add(
                new SavedCorporationWallet
                {
                    CorporationId = corporation,
                    Key = key,
                    BalanceCents = amount,
                }
            );
        }
        if (auto)
        {
            action.Add(
                new SavedCorporationBillPaymentSettings
                {
                    CorporationId = corporation,
                    DataJson = JsonSerializer.Serialize(
                        new CorporationBillPaymentSettings(corporation, key, [new(AllianceBillingRules.MaintenanceBillType, true)])
                    ),
                }
            );
        }
        action.Commit();
    }

    [Fact]
    public async Task NativePaymentUsesExecutorDivisionOnceAndPersistsTheBillLedgerAndNotification()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = Corporation(world);
        int alliance = world.Services.GetRequiredService<IAlliances>().Create(Ceo, Details()).Alliance.Id;
        var billing = world.Services.GetRequiredService<IAllianceBilling>();
        var bill = Assert.Single(billing.Bills(Ceo));
        int key = CorporationWalletRules.MasterKey + 1;
        Fund(world, corporation, bill.AmountCents * 2, key);
        using (CorporationTestClient client = new(world, 1000001))
        {
            Assert.IsType<PilotCallResult.Success>(client.CallService("allianceRegistry", "PayBill", [bill.Id, (long)key]));
            Assert.IsType<PilotCallResult.Success>(client.CallService("allianceRegistry", "PayBill", [bill.Id, (long)key]));
            var rows = Assert.IsType<BlueList<AllianceBillReply>>(
                Assert.IsType<PilotCallResult.Success>(client.CallService("allianceRegistry", "GetBills", [])).Reply
            );
            Assert.Empty(rows.Items);
        }
        Assert.Equal(bill.AmountCents, world.Services.GetRequiredService<ICorporationWallet>().Balance(Ceo, key));
        var entry = Assert.Single(
            world.Services.GetRequiredService<ICorporationWallet>().Journal(Ceo, true, key),
            row => row.EntryType == WalletEntryType.AllianceMaintenance
        );
        Assert.Equal((-bill.AmountCents, bill.Id), (entry.AmountCents, entry.ReferenceId));
        var notification = Assert.Single(world.Services.GetRequiredService<IPilotInbox>().Read(Ceo, new(Group: InboxGroup.Bills)));
        Assert.Equal((InboxNotificationType.AllianceMaintenanceBill, alliance), (notification.Type, notification.AllianceId));
        var wire = Assert.IsType<Dictionary<string, object?>>(InboxNotificationReply.Data(notification).ToBlue());
        Assert.Equal(alliance, wire["allianceID"]);
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var saved = Assert.Single(reopened.GetRequiredService<IAllianceBilling>().Bills(Ceo));
        Assert.Equal((clock.Now, corporation, key), (saved.PaidAt, saved.PaidByCorporationId, saved.DivisionKey));
        Assert.Equal(bill.AmountCents, reopened.GetRequiredService<ICorporationWallet>().Balance(Ceo, key));
        Assert.Single(reopened.GetRequiredService<IPilotInbox>().Read(Ceo, new(Group: InboxGroup.Bills)));
        await reopened.GetRequiredService<LiveWorld>().FlushAsync();
    }

    [Fact]
    public void AutomaticPaymentRenewsFromOriginalDeadlineAndSnapshotsNextCorporationCount()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int founder = Corporation(world);
        int applicant = Corporation(world, Other, "Applicant Corporation", "APPL");
        int alliance = world.Services.GetRequiredService<IAlliances>().Create(Ceo, Details()).Alliance.Id;
        var billing = world.Services.GetRequiredService<IAllianceBilling>();
        var membership = world.Services.GetRequiredService<IAllianceMembership>();
        var initial = Assert.Single(billing.Bills(Ceo));
        Fund(world, founder, initial.AmountCents * 10, auto: true);
        membership.Apply(Other, alliance, "Join");
        membership.Decide(Ceo, applicant, "Join", AllianceApplicationStatus.Accepted);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        membership.Advance();
        clock.AdvanceWallTime(TimeSpan.FromDays(29));
        membership.Advance();
        membership.Advance();
        var bills = billing.Bills(Ceo);
        Assert.Equal(2, bills.Count);
        Assert.NotNull(bills[0].PaidAt);
        Assert.Equal(initial.AmountCents * 2, bills[1].AmountCents);
        Assert.Equal(initial.DueAt + AllianceBillingRules.MaintenancePeriod.Ticks, bills[1].DueAt);
        Assert.Equal(
            initial.AmountCents * 9,
            world.Services.GetRequiredService<ICorporationWallet>().Balance(Ceo, CorporationWalletRules.MasterKey)
        );
        Assert.Single(world.Services.GetRequiredService<ICorporationWallet>().Journal(Ceo, true, CorporationWalletRules.MasterKey));
        Assert.Equal(2, world.Services.GetRequiredService<IPilotInbox>().Read(Ceo, new(Group: InboxGroup.Bills)).Count);
    }

    [Fact]
    public async Task UnpaidBillClosesAtSavedDowntimeAfterRestartAndRetainsHistoricalIdentity()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = Corporation(world);
        var alliances = world.Services.GetRequiredService<IAlliances>();
        int id = alliances.Create(Ceo, Details()).Alliance.Id;
        var billing = world.Services.GetRequiredService<IAllianceBilling>();
        long due = Assert.Single(billing.Bills(Ceo)).DueAt;
        Fund(world, corporation, 0, auto: true);
        long closing = AllianceBillingRules.NextDowntime(due);
        clock.AdvanceWallTime(TimeSpan.FromTicks(closing - clock.Now - 1));
        world.Services.GetRequiredService<IAllianceMembership>().Advance();
        Assert.Null(alliances.Read(id)!.ClosedAt);
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        var maintenance = reopened.GetRequiredService<IAllianceMembership>();
        maintenance.Advance();
        long head = maintenance.Head;
        maintenance.Advance();
        Assert.Equal(head, maintenance.Head);
        Assert.Equal(closing, reopened.GetRequiredService<IAlliances>().Read(id)!.ClosedAt);
        Assert.Null(reopened.GetRequiredService<IAlliances>().Affiliation(corporation));
        Assert.Equal("Eden Alliance", reopened.GetRequiredService<IAlliances>().Read(id)!.Details.Name);
        Assert.Throws<InvalidDataException>(() =>
            reopened.GetRequiredService<IAllianceBilling>().Pay(Ceo, 1, CorporationWalletRules.MasterKey)
        );
        await reopened.GetRequiredService<LiveWorld>().FlushAsync();
    }

    [Fact]
    public async Task LastDepartureKeepsItsOriginalClosureDeadlineAcrossRestart()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = Corporation(world);
        int id = world.Services.GetRequiredService<IAlliances>().Create(Ceo, Details()).Alliance.Id;
        world.Services.GetRequiredService<IAllianceMembership>().Remove(Ceo, corporation);
        long closing = AllianceBillingRules.NextDowntime(clock.Now);
        Assert.Null(world.Services.GetRequiredService<IAlliances>().Read(id)!.ClosedAt);
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        clock.AdvanceWallTime(TimeSpan.FromDays(3));
        reopened.GetRequiredService<IAllianceMembership>().Advance();
        Assert.Equal(closing, reopened.GetRequiredService<IAlliances>().Read(id)!.ClosedAt);
        await reopened.GetRequiredService<LiveWorld>().FlushAsync();
    }

    [Fact]
    public void RejectedPaymentLeavesBillsAndWalletsUnchangedAndWarmMaintenanceDoesNoSql()
    {
        using TestWorld world = new(liveWorld: true);
        Corporation(world);
        Corporation(world, Other, "Other Corporation", "OTH");
        int id = world.Services.GetRequiredService<IAlliances>().Create(Ceo, Details()).Alliance.Id;
        var billing = world.Services.GetRequiredService<IAllianceBilling>();
        var bill = Assert.Single(billing.Bills(Ceo));
        Assert.Throws<InvalidDataException>(() => billing.Pay(Ceo, bill.Id, CorporationWalletRules.MasterKey));
        Assert.Throws<InvalidDataException>(() => billing.Pay(Other, bill.Id, CorporationWalletRules.MasterKey));
        Assert.Equal(bill, Assert.Single(billing.Bills(Ceo)));
        Assert.Null(Assert.Single(billing.Bills(Ceo)).PaidAt);
        Assert.Equal(0, billing.Balance(Ceo));
        using var metrics = SessionPerformance.Begin("alliance idle bills", _ => { }, force: true)!;
        for (int cycle = 0; cycle < 10; cycle++)
        {
            world.Services.GetRequiredService<IAllianceMembership>().Advance();
            Assert.Null(world.Services.GetRequiredService<IAlliances>().Read(id)!.ClosedAt);
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void ExecutorHandoverChangesWhoCanPayWithoutMovingTheBillOrOldWallet()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int founder = Corporation(world);
        int applicant = Corporation(world, Other, "Applicant Corporation", "APPL");
        var alliances = world.Services.GetRequiredService<IAlliances>();
        int alliance = alliances.Create(Ceo, Details()).Alliance.Id;
        var membership = world.Services.GetRequiredService<IAllianceMembership>();
        var billing = world.Services.GetRequiredService<IAllianceBilling>();
        var bill = Assert.Single(billing.Bills(Ceo));
        Fund(world, founder, bill.AmountCents);
        Fund(world, applicant, bill.AmountCents);
        membership.Apply(Other, alliance, "Join");
        membership.Decide(Ceo, applicant, "Join", AllianceApplicationStatus.Accepted);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        membership.Advance();
        membership.DeclareSupport(Other, applicant);
        membership.DeclareSupport(Ceo, applicant);
        Assert.Throws<InvalidDataException>(() => billing.Pay(Ceo, bill.Id, CorporationWalletRules.MasterKey));
        billing.Pay(Other, bill.Id, CorporationWalletRules.MasterKey);
        Assert.Equal(applicant, Assert.Single(billing.Bills(Other)).PaidByCorporationId);
        Assert.Equal(
            bill.AmountCents,
            world.Services.GetRequiredService<ICorporationWallet>().Balance(Ceo, CorporationWalletRules.MasterKey)
        );
        Assert.Equal(0, world.Services.GetRequiredService<ICorporationWallet>().Balance(Other, CorporationWalletRules.MasterKey));
    }

    [Fact]
    public async Task OfflineAutomaticPaymentsCatchUpEachPeriodExactlyOnce()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = Corporation(world);
        world.Services.GetRequiredService<IAlliances>().Create(Ceo, Details());
        var bill = Assert.Single(world.Services.GetRequiredService<IAllianceBilling>().Bills(Ceo));
        Fund(world, corporation, bill.AmountCents * 10, auto: true);
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        clock.AdvanceWallTime(TimeSpan.FromDays(65));
        reopened.GetRequiredService<IAllianceMembership>().Advance();
        reopened.GetRequiredService<IAllianceMembership>().Advance();
        var bills = reopened.GetRequiredService<IAllianceBilling>().Bills(Ceo);
        Assert.Equal(3, bills.Count);
        Assert.Equal(2, bills.Count(b => b.PaidAt.HasValue));
        Assert.Equal(
            bill.AmountCents * 8,
            reopened.GetRequiredService<ICorporationWallet>().Balance(Ceo, CorporationWalletRules.MasterKey)
        );
        Assert.Equal(2, reopened.GetRequiredService<ICorporationWallet>().Journal(Ceo, true, CorporationWalletRules.MasterKey).Count);
        await reopened.GetRequiredService<LiveWorld>().FlushAsync();
    }
}
