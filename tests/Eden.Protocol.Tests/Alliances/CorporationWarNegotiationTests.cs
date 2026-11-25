using Eden.Data.World.Structures;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Notifications;
using Eden.Gameplay.Structures;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Alliances.AllianceCreationTests;

namespace Eden.Protocol.Tests.Alliances;

public sealed class CorporationWarNegotiationTests
{
    private const long OfferAmount = 25_000;
    private const int Master = CorporationWalletRules.MasterKey;
    private const CorporationWarNegotiationAction Accept = CorporationWarNegotiationAction.Accept;

    [Fact]
    public async Task PaidAlliesUseExactCombatWindowAndChargeOnlySuccessfulAcceptance()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var (attacker, defender, war) = StartWar(world, clock);
        TestCatalog.AddSchool(new WorldDatabase(world.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        var first = CreateAlly(world, "First Ally", "FALT", 2);
        var second = CreateAlly(world, "Second Ally", "SALT", 3);
        var wars = world.Services.GetRequiredService<ICorporationWars>();
        var wallets = world.Services.GetRequiredService<ICorporationWallet>();
        var bills = world.Services.GetRequiredService<ICorporationBilling>();
        var live = world.Services.GetRequiredService<LiveWorld>();
        long start = clock.Now + CorporationWarRules.AllyWarmup;
        long finish = clock.Now + CorporationWarRules.AllyLifetime;

        var firstOffer = OfferAlly(wars, first.PilotId, defender, war.Id);
        AllianceBillingTests.Fund(world, defender, OfferAmount);
        wars.ResolveNegotiation(Other, firstOffer, CorporationWarNegotiationKind.AllyOffer, Accept);
        wars.ResolveNegotiation(Other, firstOffer, CorporationWarNegotiationKind.AllyOffer, Accept);
        Assert.Equal(0, wallets.Balance(Other, Master));
        Assert.Equal(OfferAmount, wallets.Balance(first.PilotId, Master));

        var secondOffer = OfferAlly(wars, second.PilotId, defender, war.Id);
        long cost = wars.AllyBaseCost(Other);
        AllianceBillingTests.Fund(world, defender, OfferAmount + cost - 1);
        Assert.Throws<InvalidDataException>(() =>
            wars.ResolveNegotiation(Other, secondOffer, CorporationWarNegotiationKind.AllyOffer, Accept)
        );
        Assert.Equal(OfferAmount + cost - 1, wallets.Balance(Other, Master));
        Assert.Equal(0, wallets.Balance(second.PilotId, Master));
        AllianceBillingTests.Fund(world, defender, OfferAmount + cost);
        wars.ResolveNegotiation(Other, secondOffer, CorporationWarNegotiationKind.AllyOffer, Accept);
        Assert.Equal(0, wallets.Balance(Other, Master));
        Assert.Equal(OfferAmount, wallets.Balance(second.PilotId, Master));
        Assert.Equal(2, wars.War(war.Id)!.Allies.Count);

        Assert.False(live.Economy.Inspect(view => CorporationWarStore.HasActiveWar(view, attacker, first.CorporationId, start - 1)));
        Assert.True(live.Economy.Inspect(view => CorporationWarStore.HasActiveWar(view, attacker, first.CorporationId, start)));
        Assert.True(live.Economy.Inspect(view => CorporationWarStore.HasActiveWar(view, first.CorporationId, attacker, start)));
        Assert.False(live.Economy.Inspect(view => CorporationWarStore.HasActiveWar(view, defender, first.CorporationId, start)));
        Assert.True(live.Economy.Inspect(view => CorporationWarStore.ParticipatesInActiveWar(view, first.CorporationId, start)));

        // Keep the war paid past the ally's earlier expiry so the boundaries are independent.
        var nextBill = Assert.Single(bills.WarInvoices(Ceo));
        Assert.True(bills.PayWarInvoice(Ceo, nextBill.Id, Master));
        clock.AdvanceWallTime(TimeSpan.FromTicks(nextBill.DueAt - clock.Now));
        Assert.True(bills.PayWarInvoice(Ceo, Assert.Single(bills.WarInvoices(Ceo)).Id, Master));
        Assert.True(live.Economy.Inspect(view => CorporationWarStore.HasActiveWar(view, attacker, first.CorporationId, finish - 1)));
        Assert.False(live.Economy.Inspect(view => CorporationWarStore.HasActiveWar(view, attacker, first.CorporationId, finish)));
        Assert.True(live.Economy.Inspect(view => CorporationWarStore.HasActiveWar(view, attacker, defender, finish)));

        await live.FlushAsync();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var saved = reopened.GetRequiredService<ICorporationWars>();
        var persistedWar = Assert.Single(saved.Wars(first.CorporationId));
        Assert.Equal(war.Id, persistedWar.Id);
        Assert.Equal(
            new CorporationWarAlly(first.CorporationId, start, finish),
            Assert.Single(persistedWar.Allies, ally => ally.OwnerId == first.CorporationId)
        );
        Assert.Equal(CorporationWarNegotiationState.Accepted, saved.Negotiation(Other, secondOffer)!.State);
    }

    [Fact]
    public async Task SurrenderPaymentCooldownAndBilateralTruceSurviveCheckpoint()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var (attacker, defender, war) = StartWar(world, clock);
        var wars = world.Services.GetRequiredService<ICorporationWars>();
        var wallets = world.Services.GetRequiredService<ICorporationWallet>();
        var live = world.Services.GetRequiredService<LiveWorld>();
        long attackerBalance = wallets.Balance(Ceo, Master);
        wars.CreateSurrenderOffer(Other, war.Id, OfferAmount, "Cease hostilities");
        long id = Assert.Single(wars.Negotiations(Ceo)).Id;
        AllianceBillingTests.Fund(world, defender, OfferAmount - 1);
        Assert.Throws<InvalidDataException>(() => wars.ResolveNegotiation(Ceo, id, CorporationWarNegotiationKind.Surrender, Accept));
        Assert.Null(wars.War(war.Id)!.EndsAt);
        Assert.Equal(attackerBalance, wallets.Balance(Ceo, Master));
        AllianceBillingTests.Fund(world, defender, OfferAmount);
        long treatyExpires = clock.Now + CorporationWarRules.SurrenderTruce;
        long cooldownEnds = clock.Now + CorporationWarRules.Cooldown;
        wars.ResolveNegotiation(Ceo, id, CorporationWarNegotiationKind.Surrender, Accept);
        wars.ResolveNegotiation(Ceo, id, CorporationWarNegotiationKind.Surrender, Accept);
        Assert.Equal(attackerBalance + OfferAmount, wallets.Balance(Ceo, Master));
        Assert.Equal(0, wallets.Balance(Other, Master));
        Assert.Equal(cooldownEnds, wars.War(war.Id)!.EndsAt);
        Assert.True(live.Economy.Inspect(view => CorporationWarStore.HasActiveWar(view, attacker, defender, cooldownEnds - 1)));
        Assert.False(live.Economy.Inspect(view => CorporationWarStore.HasActiveWar(view, attacker, defender, cooldownEnds)));
        Assert.Single(
            world.Services.GetRequiredService<IPilotInbox>().Read(Other, new(Group: InboxGroup.War)),
            notice => notice.Type == InboxNotificationType.WarSurrenderAccepted
        );

        await live.FlushAsync();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var saved = reopened.GetRequiredService<ICorporationWars>();
        var treaties = saved.PeaceTreaties(Other);
        Assert.Equal(2, treaties.Count);
        Assert.All(
            treaties,
            treaty =>
                Assert.Equal(
                    (attacker, treatyExpires, CorporationWarPeaceReason.Surrender),
                    (treaty.OtherOwnerId, treaty.ExpiresAt, treaty.Reason)
                )
        );
        Assert.Throws<InvalidDataException>(() => saved.Declare(Ceo, defender, CorporationWarTests.Headquarters, false));
        Assert.Throws<InvalidDataException>(() => saved.SendInvitation(Other, attacker));
        clock.AdvanceWallTime(TimeSpan.FromTicks(CorporationWarRules.SurrenderTruce));
        Assert.Empty(saved.PeaceTreaties(Ceo));
        Assert.NotEqual(war.Id, saved.Declare(Ceo, defender, CorporationWarTests.Headquarters, false).Id);
    }

    private static (int Attacker, int Defender, CorporationWar War) StartWar(TestWorld world, TestClock clock)
    {
        int attacker = Corporation(world);
        int defender = Corporation(world, Other, "Defending Corporation", "DEFC");
        CorporationWarTests.SeedHeadquarters(world, attacker, defender);
        AllianceBillingTests.Fund(world, attacker, 4 * CorporationWarRules.WeeklyFeeCents);
        var wars = world.Services.GetRequiredService<ICorporationWars>();
        var war = wars.Declare(Ceo, defender, CorporationWarTests.Headquarters, false);
        clock.AdvanceWallTime(TimeSpan.FromTicks(CorporationWarRules.Warmup));
        return (attacker, defender, war);
    }

    private static (long PilotId, int CorporationId) CreateAlly(TestWorld world, string name, string ticker, int structureOffset)
    {
        long pilot = world.Services.GetRequiredService<ICharacterCreation>().Create(1000002, new(name, 1, 1, 1, 64, [1], [2], 18));
        int corporation = Corporation(world, pilot, name + " Corporation", ticker);
        using var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None);
        action.Add(
            new SavedStructure
            {
                Id = CorporationWarTests.Headquarters + structureOffset,
                TypeId = 35832,
                Name = name + " Headquarters",
                CorporationId = corporation,
                SystemId = 30000142,
                HasCore = true,
                State = StructureState.ShieldVulnerable,
            }
        );
        action.Commit();
        return (pilot, corporation);
    }

    private static long OfferAlly(ICorporationWars wars, long pilot, int defender, long war)
    {
        wars.CreateAllyOffer(pilot, war, defender, OfferAmount, "Offer help");
        return Assert.Single(wars.Negotiations(pilot)).Id;
    }
}
