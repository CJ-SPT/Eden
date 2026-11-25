using Eden.Data.World.Corporations;
using Eden.Data.World.Structures;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Gameplay.Notifications;
using Eden.Gameplay.Structures;
using Eden.Protocol.Notifications;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Alliances.AllianceCreationTests;

namespace Eden.Protocol.Tests.Alliances;

public sealed class CorporationWarTests
{
    internal const long Headquarters = 1_030_000_000_001;
    private const long DefenseStructure = Headquarters + 1;
    private const int System = 30000142;

    [Fact]
    public async Task DeclarationAndWeeklyInvoiceCommitOnceAndSurviveRestart()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int attacker = Corporation(world);
        int defender = Corporation(world, Other, "Defenders", "DFN");
        SeedHeadquarters(world, attacker, defender);
        long fee = CorporationWarRules.WeeklyFeeCents;
        AllianceBillingTests.Fund(world, attacker, fee * 3);
        var wars = world.Services.GetRequiredService<ICorporationWars>();
        Assert.True(wars.IsEligible(attacker));
        Assert.True(wars.IsEligible(defender));
        Assert.Equal(Headquarters, Assert.Single(wars.Headquarters(Ceo, attacker)).StructureId);
        Assert.Throws<InvalidDataException>(() => wars.Declare(Ceo, defender, DefenseStructure, false));
        var declared = wars.Declare(Ceo, defender, Headquarters, false);
        Assert.Equal(
            (attacker, defender, clock.Now + CorporationWarRules.Warmup),
            (declared.AttackerId, declared.DefenderId, declared.StartsAt)
        );
        Assert.Throws<InvalidDataException>(() => wars.Declare(Other, attacker, DefenseStructure, false));
        Assert.Equal(fee * 2, world.Services.GetRequiredService<ICorporationWallet>().Balance(Ceo, CorporationWalletRules.MasterKey));

        var firstBill = Assert.Single(world.Services.GetRequiredService<ICorporationBilling>().WarInvoices(Ceo));
        Assert.Equal(clock.Now + CorporationWarRules.BillingPeriod, firstBill.DueAt);
        using (CorporationTestClient client = new(world, 1000001))
        {
            var success = Assert.IsType<PilotCallResult.Success>(client.CallService("billMgr", "GetCorporationBills", []));
            var bills = Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(success.Reply)));
            var row = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(Assert.Single(bills)).State);
            Assert.Equal(firstBill.Id, row["billID"]);
            Assert.Equal(declared.Id, row["externalID2"]);
            Assert.IsType<PilotCallResult.Success>(client.CallService("billMgr", "PayCorporationBill", [firstBill.Id, 1000L]));
            _ = client.CallService("billMgr", "PayCorporationBill", [firstBill.Id, 1000L]);
        }
        using (CorporationTestClient defenderClient = new(world, 1000002))
        {
            Assert.IsType<PilotCallResult.Failure>(defenderClient.CallService("billMgr", "PayCorporationBill", [firstBill.Id, 1000L]));
        }
        var wallet = world.Services.GetRequiredService<ICorporationWallet>();
        Assert.Equal(fee, wallet.Balance(Ceo, CorporationWalletRules.MasterKey));
        var journal = wallet
            .Journal(Ceo, true, CorporationWalletRules.MasterKey)
            .Where(e => e.EntryType == WalletEntryType.WarFee)
            .ToArray();
        Assert.Equal(2, journal.Length);
        Assert.All(journal, row => Assert.Equal(-fee, row.AmountCents));
        var declaration = Assert.Single(world.Services.GetRequiredService<IPilotInbox>().Read(Other, new(Group: InboxGroup.War)));
        Assert.Equal(InboxNotificationType.WarDeclared, declaration.Type);
        var data = Assert.IsType<Dictionary<string, object?>>(InboxNotificationReply.Data(declaration).ToBlue());
        Assert.Equal(declared.StartsAt, data["timeStarted"]);

        await world.Services.GetRequiredService<LiveWorld>().FlushAsync();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var saved = reopened.GetRequiredService<ICorporationWars>();
        Assert.Equal(declared.Id, Assert.Single(saved.Wars(attacker)).Id);
        Assert.Equal(declared.Id, Assert.Single(saved.WarsForStructure(Headquarters)).Id);
        clock.AdvanceWallTime(TimeSpan.FromTicks(CorporationWarRules.BillingPeriod));
        var upkeep = reopened.GetRequiredService<ICorporationWarMaintenance>();
        upkeep.Advance(clock.Now);
        var renewal = Assert.Single(reopened.GetRequiredService<ICorporationBilling>().WarInvoices(Ceo));
        Assert.NotEqual(firstBill.Id, renewal.Id);
        Assert.Equal(firstBill.DueAt + CorporationWarRules.BillingPeriod, renewal.DueAt);
        Assert.Null(saved.War(declared.Id)!.EndsAt);

        clock.AdvanceWallTime(TimeSpan.FromTicks(CorporationWarRules.BillingPeriod));
        upkeep.Advance(clock.Now);
        upkeep.Advance(clock.Now);
        var ended = saved.War(declared.Id)!;
        Assert.Equal(CorporationWarEndReason.BillUnpaid, ended.ReasonEnded);
        Assert.Equal(renewal.DueAt + CorporationWarRules.Cooldown, ended.EndsAt);
        Assert.Empty(reopened.GetRequiredService<ICorporationBilling>().WarInvoices(Ceo));
        var inbox = reopened.GetRequiredService<IPilotInbox>();
        var endNotice = Assert.Single(
            inbox.Read(Other, new(Group: InboxGroup.War)),
            n => n.Type == InboxNotificationType.WarRetractedByConcord
        );
        Assert.Equal(ended.EndsAt, endNotice.CorporationWar!.EndsAt);
    }

    [Fact]
    public void MutualInvitationNeedsConsentAndUsesTheSameWarmupWithoutBilling()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int attacker = Corporation(world);
        int defender = Corporation(world, Other, "Defenders", "DFN");
        var wars = world.Services.GetRequiredService<ICorporationWars>();
        Assert.False(wars.IsEligible(attacker));
        Assert.False(wars.IsEligible(defender));
        wars.SetInvitationsBlocked(Other, true);
        Assert.Throws<InvalidDataException>(() => wars.SendInvitation(Ceo, defender));
        wars.SetInvitationsBlocked(Other, false);
        wars.SendInvitation(Ceo, defender);
        var invitation = Assert.Single(wars.Invitations(Other));
        Assert.Equal((attacker, defender), (invitation.FromOwnerId, invitation.ToOwnerId));
        Assert.Empty(wars.Wars(attacker));
        wars.RespondInvitation(Other, attacker, true);
        var war = Assert.Single(wars.Wars(attacker));
        Assert.True(war.Mutual);
        Assert.Null(war.HeadquartersId);
        Assert.Equal(clock.Now + CorporationWarRules.Warmup, war.StartsAt);
        Assert.Empty(world.Services.GetRequiredService<ICorporationBilling>().WarInvoices(Ceo));
        clock.AdvanceWallTime(TimeSpan.FromTicks(CorporationWarRules.Warmup));
        wars.RetractMutual(Other, war.Id);
        Assert.Equal(clock.Now + CorporationWarRules.Cooldown, wars.War(war.Id)!.EndsAt);
    }

    [Fact]
    public void LosingHeadquartersStartsCooldownAtLossAndBlocksImmediateRedeclaration()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int attacker = Corporation(world);
        int defender = Corporation(world, Other, "Defenders", "DFN");
        SeedHeadquarters(world, attacker, defender);
        AllianceBillingTests.Fund(world, attacker, CorporationWarRules.WeeklyFeeCents * 2);
        var wars = world.Services.GetRequiredService<ICorporationWars>();
        var declared = wars.Declare(Ceo, defender, Headquarters, false);
        clock.AdvanceWallTime(TimeSpan.FromDays(2));
        long loss = clock.Now;
        using (var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None))
        {
            var lost = action.Set<SavedStructure>().Find(Headquarters)!;
            lost.State = StructureState.Destroyed;
            lost.StateStartedAt = loss;
            action.Add(Structure(Headquarters + 2, attacker));
            action.Commit();
        }
        clock.AdvanceWallTime(TimeSpan.FromHours(3));
        world.Services.GetRequiredService<ICorporationWarMaintenance>().Advance(clock.Now);
        var end = wars.War(declared.Id)!;
        Assert.Equal(CorporationWarEndReason.HeadquartersLost, end.ReasonEnded);
        Assert.Equal(loss + CorporationWarRules.Cooldown, end.EndsAt);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        var treaty = Assert.Single(wars.PeaceTreaties(Ceo));
        Assert.Equal(defender, treaty.OtherOwnerId);
        Assert.Throws<InvalidDataException>(() => wars.Declare(Ceo, defender, Headquarters + 2, false));
    }

    [Fact]
    public async Task FormingAllianceAdoptsDefensiveWarAndKeepsItsOriginalDeadline()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int attacker = Corporation(world);
        int defender = Corporation(world, Other, "Defenders", "DFN");
        SeedHeadquarters(world, attacker, defender);
        AllianceBillingTests.Fund(world, attacker, CorporationWarRules.WeeklyFeeCents);
        var wars = world.Services.GetRequiredService<ICorporationWars>();
        var declared = wars.Declare(Ceo, defender, Headquarters, false);
        var alliances = world.Services.GetRequiredService<IAlliances>();
        Assert.Throws<InvalidDataException>(() => alliances.Create(Ceo, Details()));
        int alliance = alliances.Create(Other, Details()).Alliance.Id;
        var adopted = Assert.Single(wars.Wars(alliance));
        Assert.Equal((declared.Id, declared.StartsAt, declared.BillId), (adopted.Id, adopted.StartsAt, adopted.BillId));
        Assert.Equal(alliance, adopted.DefenderId);
        Assert.Empty(wars.Wars(defender));

        await world.Services.GetRequiredService<LiveWorld>().FlushAsync();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var saved = reopened.GetRequiredService<ICorporationWars>().War(declared.Id)!;
        Assert.Equal(alliance, saved.DefenderId);
        Assert.Equal(declared.StartsAt, saved.StartsAt);
    }

    [Fact]
    public void AllianceInvoiceUsesExecutorWalletAndRenewsFromTheDueTime()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int corporation = Corporation(world);
        int defender = Corporation(world, Other, "Defenders", "DFN");
        SeedHeadquarters(world, corporation, defender);
        int alliance = world.Services.GetRequiredService<IAlliances>().Create(Ceo, Details()).Alliance.Id;
        long fee = CorporationWarRules.WeeklyFeeCents;
        AllianceBillingTests.Fund(world, corporation, fee * 3);
        var wars = world.Services.GetRequiredService<ICorporationWars>();
        Assert.Throws<InvalidDataException>(() => wars.Declare(Ceo, defender, Headquarters, false));
        var declared = wars.Declare(Ceo, defender, Headquarters, true);
        Assert.Equal(alliance, declared.AttackerId);
        var billing = world.Services.GetRequiredService<ICorporationBilling>();
        Assert.Empty(billing.WarInvoices(Ceo));
        var invoice = Assert.Single(billing.WarInvoices(Ceo, alliance: true));
        Assert.Equal(alliance, invoice.DebtorId);
        using CorporationTestClient client = new(world, 1000001);
        Assert.IsType<PilotCallResult.Success>(client.CallService("allianceRegistry", "PayBill", [invoice.Id, 1000L]));
        Assert.Equal(fee, world.Services.GetRequiredService<ICorporationWallet>().Balance(Ceo, CorporationWalletRules.MasterKey));
        Assert.Empty(billing.WarInvoices(Ceo, alliance: true));

        clock.AdvanceWallTime(TimeSpan.FromTicks(CorporationWarRules.BillingPeriod));
        var renewed = Assert.Single(billing.WarInvoices(Ceo, alliance: true));
        Assert.Equal(invoice.DueAt + CorporationWarRules.BillingPeriod, renewed.DueAt);
        Assert.NotEqual(invoice.Id, renewed.Id);
        Assert.IsType<PilotCallResult.Failure>(client.CallService("allianceRegistry", "PayBill", [invoice.Id, 1000L]));
        Assert.Equal(renewed.Id, Assert.Single(billing.WarInvoices(Ceo, alliance: true)).Id);
    }

    [Fact]
    public void WartimeDepartureBlocksRejoiningUntilSevenDaysOrHostilitiesEnd()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int attacker = Corporation(world);
        int defender = Corporation(world, Other, "Defenders", "DFN");
        var wars = world.Services.GetRequiredService<ICorporationWars>();
        wars.SendInvitation(Ceo, defender);
        wars.RespondInvitation(Other, attacker, true);
        var war = Assert.Single(wars.Wars(defender));
        clock.AdvanceWallTime(TimeSpan.FromDays(2));
        var economy = world.Services.GetRequiredService<LiveWorld>().Economy;
        using (var action = economy.Begin(TransactionParticipants.None))
        {
            action.Add(
                new SavedCorporationEmployment
                {
                    Id = action.Next("WorldCorporationEmployment"),
                    PilotId = Ceo,
                    CorporationId = defender,
                    StartedAt = war.DeclaredAt,
                }
            );
            action.Add(
                new SavedCorporationEmployment
                {
                    Id = action.Next("WorldCorporationEmployment"),
                    PilotId = Ceo,
                    CorporationId = attacker,
                    StartedAt = clock.Now,
                }
            );
            action.Commit();
        }
        Assert.True(economy.Inspect(action => CorporationWarStore.PreventsRejoining(action, Ceo, defender, clock.Now)));
        Assert.False(
            economy.Inspect(action => CorporationWarStore.PreventsRejoining(action, Ceo, defender, clock.Now + TimeSpan.TicksPerDay * 7))
        );
        wars.RetractMutual(Other, war.Id);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        Assert.False(economy.Inspect(action => CorporationWarStore.PreventsRejoining(action, Ceo, defender, clock.Now)));
    }

    internal static void SeedHeadquarters(TestWorld world, int attacker, int defender)
    {
        string catalogPath = new WorldDatabase(world.Path).CatalogPath;
        PassiveCatalogFixture.LoadStructures(catalogPath);
        using (CatalogDbContext catalog = new(catalogPath, writable: true))
        {
            const string system = """{"securityStatus":0.9}""";
            const string content = """{"security":0.9}""";
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142',{system})");
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/solarsystemcontent','30000142',{content})");
        }
        using var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None);
        action.Add(Structure(Headquarters, attacker));
        action.Add(Structure(DefenseStructure, defender));
        action.Commit();
    }

    private static SavedStructure Structure(long id, int owner) =>
        new()
        {
            Id = id,
            TypeId = 35832,
            Name = "War Headquarters",
            CorporationId = owner,
            SystemId = System,
            HasCore = true,
            State = StructureState.ShieldVulnerable,
        };
}
