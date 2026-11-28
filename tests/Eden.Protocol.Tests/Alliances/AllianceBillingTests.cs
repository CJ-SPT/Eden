using System.Text.Json;
using Eden.Data.Catalog;
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
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
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
            int alliance = action.Set<SavedAllianceMember>().Find(corporation)!.AllianceId;
            action.Set<SavedAlliance>().Find(alliance)!.AutomaticMaintenancePayment = true;
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
        world.Services.GetRequiredService<ICorporationWallet>().SelectDivision(Ceo, key);
        using (CorporationTestClient client = new(world, 1000001))
        {
            var balance = Assert.IsType<PilotCallResult.Success>(client.CallService("allianceRegistry", "GetBillBalance", [bill.Id]));
            Assert.Equal(bill.AmountCents * 2 / (double)MarketDefinitions.CentsPerIsk, balance.Reply.ToBlue());
            Assert.IsType<PilotCallResult.Failure>(client.CallService("allianceRegistry", "GetBillBalance", [0L]));
            var rowsBefore = Assert.IsType<BlueList<BlueModel>>(
                Assert.IsType<PilotCallResult.Success>(client.CallService("allianceRegistry", "GetBills", [])).Reply
            );
            var row = Assert.IsType<AllianceBillReply>(Assert.Single(rowsBefore.Items));
            var fields = BlueRead.Fields(Assert.IsType<BlueInstance>(row.ToBlue()).State);
            Assert.Equal(alliance, fields["externalID"]);
            Assert.Equal(-1, fields["externalID2"]);
            Assert.IsType<PilotCallResult.Success>(client.CallService("allianceRegistry", "PayBill", [bill.Id, (long)key]));
            Assert.IsType<PilotCallResult.Success>(client.CallService("allianceRegistry", "PayBill", [bill.Id, (long)key]));
            var rows = Assert.IsType<BlueList<BlueModel>>(
                Assert.IsType<PilotCallResult.Success>(client.CallService("allianceRegistry", "GetBills", [])).Reply
            );
            Assert.Empty(rows.Items);
        }
        Assert.Equal(bill.AmountCents, world.Services.GetRequiredService<ICorporationWallet>().Balance(Ceo, key));
        var entry = Assert.Single(
            world.Services.GetRequiredService<ICorporationWallet>().Journal(Ceo, true, key),
            row => row.EntryType == WalletEntryType.AllianceMaintenance
        );
        Assert.Equal((-bill.AmountCents, (long)alliance), (entry.AmountCents, entry.ReferenceId));
        var notification = Assert.Single(world.Services.GetRequiredService<IPilotInbox>().Read(Ceo, new(Group: InboxGroup.Bills)));
        Assert.Equal((InboxNotificationType.AllianceMaintenanceBill, alliance), (notification.Type, notification.AllianceId));
        var wire = Assert.IsType<Dictionary<string, object?>>(InboxNotificationReply.Data(notification).ToBlue());
        Assert.Equal(alliance, wire["allianceID"]);
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var saved = Assert.Single(reopened.GetRequiredService<IAllianceBilling>().Bills(Ceo));
        Assert.Equal((clock.Now, corporation, key), (saved.PaidAt, saved.PaidByCorporationId, saved.DivisionKey));
        var reopenedWallet = reopened.GetRequiredService<ICorporationWallet>();
        Assert.Equal(bill.AmountCents, reopenedWallet.Balance(Ceo, key));
        Assert.Equal((long)alliance, Assert.Single(reopenedWallet.Journal(Ceo, true, key)).ReferenceId);
        Assert.Single(reopened.GetRequiredService<IPilotInbox>().Read(Ceo, new(Group: InboxGroup.Bills)));
        await reopened.GetRequiredService<LiveWorld>().FlushAsync();
    }

    [Fact]
    public async Task NativeAutomaticPaymentSettingsUseAllianceDebtorAndExecutorDivisionAcrossRestart()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = Corporation(world);
        int alliance = world.Services.GetRequiredService<IAlliances>().Create(Ceo, Details()).Alliance.Id;
        var bill = Assert.Single(world.Services.GetRequiredService<IAllianceBilling>().Bills(Ceo));
        int key = CorporationWalletRules.MasterKey + 1;
        Fund(world, corporation, bill.AmountCents, key);
        using (CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO SourceRecord(Dataset,Key,Data) VALUES
                    ('client/billtypes','2','{{"billTypeName":"Rental Bill"}}'),
                    ('client/billtypes','5','{{"billTypeName":"Alliance Maintenance"}}');
                """
            );
        }
        using (CorporationTestClient client = new(world, 1000001))
        {
            var initial = ReadAutomaticPaymentSettings(client);
            var initialRules = Assert.IsType<Dictionary<long, object?>>(initial[alliance]);
            Assert.Equal(false, initialRules[AllianceBillingRules.MaintenanceBillType]);
            Dictionary<long, object?> snapshot = new()
            {
                [corporation] = new BlueDictionary([("divisionID", (long)key), (2L, false)]),
                [alliance] = new Dictionary<long, object?> { [AllianceBillingRules.MaintenanceBillType] = true },
            };
            Assert.IsType<PilotCallResult.Success>(client.CallService("billMgr", "SendAutomaticPaySettings", [snapshot]));
            var saved = ReadAutomaticPaymentSettings(client);
            Assert.Equal(2, saved.Count);
            var corporationFields = Assert.IsType<BlueDictionary>(saved[corporation]).Entries;
            Assert.Contains(
                corporationFields,
                field => field.Key is (string or byte[]) && BlueMarshal.Text(field.Key) == "divisionID" && Equals(field.Value, (long)key)
            );
            Assert.DoesNotContain(corporationFields, field => Equals(field.Key, (long)AllianceBillingRules.MaintenanceBillType));
            var allianceRules = Assert.IsType<Dictionary<long, object?>>(saved[alliance]);
            Assert.Equal(true, allianceRules[AllianceBillingRules.MaintenanceBillType]);
            Dictionary<long, object?> forged = new()
            {
                [corporation] = new BlueDictionary([("divisionID", (long)CorporationWalletRules.MasterKey), (2L, true)]),
                [alliance + 1] = new Dictionary<long, object?> { [AllianceBillingRules.MaintenanceBillType] = false },
            };
            Assert.IsType<PilotCallResult.Failure>(client.CallService("billMgr", "SendAutomaticPaySettings", [forged]));
        }
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var preferences = reopened.GetRequiredService<ICorporationBilling>().Read(Ceo);
        Assert.Equal(key, preferences.DivisionKey);
        Assert.Contains(preferences.Rules, rule => rule.BillTypeId == AllianceBillingRules.MaintenanceBillType && rule.Enabled);
        clock.AdvanceWallTime(TimeSpan.FromTicks(bill.DueAt - clock.Now));
        reopened.GetRequiredService<IAllianceMembership>().Advance();
        Assert.NotNull(reopened.GetRequiredService<IAllianceBilling>().Bills(Ceo)[0].PaidAt);
        var wallet = reopened.GetRequiredService<ICorporationWallet>();
        Assert.Equal(0, wallet.Balance(Ceo, key));
        Assert.Single(wallet.Journal(Ceo, true, key));
        await reopened.GetRequiredService<LiveWorld>().FlushAsync();
    }

    private static Dictionary<long, object?> ReadAutomaticPaymentSettings(CorporationTestClient client)
    {
        var reply = Assert.IsType<PilotCallResult.Success>(client.CallService("billMgr", "GetAutomaticPaySettings", [])).Reply;
        return Assert.IsType<Dictionary<long, object?>>(BlueMarshal.Decode(BlueMarshal.Encode(reply)));
    }

    [Fact]
    public void AutomaticPaymentSurvivesExecutorHandoverWithoutFollowingTheFormerExecutor()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int founder = Corporation(world);
        int successor = Corporation(world, Other, "Successor Corporation", "SUCC");
        var alliances = world.Services.GetRequiredService<IAlliances>();
        var membership = world.Services.GetRequiredService<IAllianceMembership>();
        var billing = world.Services.GetRequiredService<ICorporationBilling>();
        var bills = world.Services.GetRequiredService<IAllianceBilling>();
        var wallet = world.Services.GetRequiredService<ICorporationWallet>();
        int oldAlliance = alliances.Create(Ceo, Details()).Alliance.Id;
        var initial = Assert.Single(bills.Bills(Ceo));
        int master = CorporationWalletRules.MasterKey;
        int division = master + 1;
        Fund(world, founder, initial.AmountCents);
        Fund(world, successor, initial.AmountCents, division);
        billing.Save(Ceo, new(founder, master, []), new(oldAlliance, true));
        billing.Save(Other, new(successor, division, []));
        membership.Apply(Other, oldAlliance, "Join");
        membership.Decide(Ceo, successor, "Join", AllianceApplicationStatus.Accepted);
        clock.AdvanceWallTime(AllianceMembershipRules.AdmissionDelay);
        membership.Advance();
        membership.DeclareSupport(Other, successor);
        membership.DeclareSupport(Ceo, successor);
        membership.Remove(Ceo, founder);
        int newAlliance = alliances.Create(Ceo, Details("Second Alliance", "SEC")).Alliance.Id;
        var second = Assert.Single(bills.Bills(Ceo));
        using (CorporationTestClient founderClient = new(world, 1000001))
        using (CorporationTestClient successorClient = new(world, 1000002))
        {
            var oldRules = Assert.IsType<Dictionary<long, object?>>(ReadAutomaticPaymentSettings(successorClient)[oldAlliance]);
            var newRules = Assert.IsType<Dictionary<long, object?>>(ReadAutomaticPaymentSettings(founderClient)[newAlliance]);
            Assert.Equal(true, oldRules[AllianceBillingRules.MaintenanceBillType]);
            Assert.Equal(false, newRules[AllianceBillingRules.MaintenanceBillType]);
        }
        clock.AdvanceWallTime(TimeSpan.FromTicks(second.DueAt - clock.Now));
        membership.Advance();
        Assert.Equal(successor, bills.Bills(Other).First(bill => bill.Id == initial.Id).PaidByCorporationId);
        Assert.Null(bills.Bills(Ceo).First(bill => bill.Id == second.Id).PaidAt);
        Assert.Equal(initial.AmountCents, wallet.Balance(Ceo, master));
        Assert.Equal(0, wallet.Balance(Other, division));
    }

    [Fact]
    public void Version240MigratesLegacyAutomaticPaymentToTheCurrentAlliance()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int founder = Corporation(world);
        int alliance = world.Services.GetRequiredService<IAlliances>().Create(Ceo, Details()).Alliance.Id;
        world.Services.Dispose();
        int division = CorporationWalletRules.MasterKey + 1;
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Database.ExecuteSqlRaw(
                """
                ALTER TABLE WorldAlliance DROP COLUMN LastExecutorCorporationId;
                ALTER TABLE WorldAlliance DROP COLUMN AutomaticMaintenancePayment;
                PRAGMA user_version=240;
                """
            );
            string legacy = JsonSerializer.Serialize(
                new CorporationBillPaymentSettings(founder, division, [new(2, true), new(AllianceBillingRules.MaintenanceBillType, true)])
            );
            saved.Database.ExecuteSql(
                $"INSERT INTO WorldCorporationBillPaymentSettings(CorporationId,DataJson) VALUES({founder},{legacy})"
            );
        }
        WorldDatabase database = new(world.Path);
        WorldInitialization.Initialize(database);
        using WorldDbContext reopened = new(world.Path);
        var migrated = reopened.Set<SavedAlliance>().Find(alliance)!;
        Assert.True(migrated.AutomaticMaintenancePayment);
        Assert.Equal(founder, migrated.LastExecutorCorporationId);
        var settings = JsonSerializer.Deserialize<CorporationBillPaymentSettings>(
            reopened.Set<SavedCorporationBillPaymentSettings>().Find(founder)!.DataJson
        )!;
        Assert.Equal(division, settings.DivisionKey);
        Assert.Equal(new CorporationBillPaymentRule(2, true), Assert.Single(settings.Rules));
        Assert.Equal(WorldMigration.CurrentVersion, reopened.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
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

    [Theory]
    [InlineData(28, 2)]
    [InlineData(30, 1)]
    public async Task LateMaintenanceKeepsBillingAndAdmissionInDeadlineOrder(int acceptedDay, int nextInvoiceCorporations)
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int founder = Corporation(world);
        int applicant = Corporation(world, Other, "Applicant Corporation", "APPL");
        int alliance = world.Services.GetRequiredService<IAlliances>().Create(Ceo, Details()).Alliance.Id;
        var initial = Assert.Single(world.Services.GetRequiredService<IAllianceBilling>().Bills(Ceo));
        Fund(world, founder, initial.AmountCents, auto: true);
        clock.AdvanceWallTime(TimeSpan.FromDays(acceptedDay));
        var membership = world.Services.GetRequiredService<IAllianceMembership>();
        membership.Apply(Other, alliance, "Join");
        membership.Decide(Ceo, applicant, "Join", AllianceApplicationStatus.Accepted);
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        clock.AdvanceWallTime(TimeSpan.FromDays(32 - acceptedDay));
        var bills = reopened.GetRequiredService<IAllianceBilling>().Bills(Ceo);
        Assert.Collection(
            bills,
            paid => Assert.NotNull(paid.PaidAt),
            next =>
                Assert.Equal(
                    (initial.DueAt, nextInvoiceCorporations, initial.AmountCents * nextInvoiceCorporations),
                    (next.IssuedAt, next.CorporationCount, next.AmountCents)
                )
        );
        Assert.Equal(alliance, reopened.GetRequiredService<IAlliances>().Affiliation(applicant));
        await reopened.GetRequiredService<LiveWorld>().FlushAsync();
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
        using (CorporationTestClient member = new(world, 1000002))
        {
            var response = Assert.IsType<PilotCallResult.Success>(member.CallService("allianceRegistry", "GetBills", []));
            Assert.Single(Assert.IsType<BlueList<BlueModel>>(response.Reply).Items);
            Assert.IsType<PilotCallResult.Success>(member.CallService("allianceRegistry", "GetBillsReceivable", []));
            Assert.IsType<PilotCallResult.Failure>(
                member.CallService("allianceRegistry", "PayBill", [bill.Id, (long)CorporationWalletRules.MasterKey])
            );
        }
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
