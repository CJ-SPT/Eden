using Eden.Data.World.Market;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Gameplay.Redemption;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Alliances.AllianceCreationTests;
using static Eden.Protocol.Tests.Alliances.AllianceSocialTests;

namespace Eden.Protocol.Tests.Alliances;

public sealed class AllianceEverMarkTests
{
    private const int Issuer = CurrencyRewardRules.ParagonCorporation;
    private const string FromPilot = "TransferLPFromMyWalletToOtherCorp";
    private const string FromCorporation = "TransferLPFromMyCorpWalletToOtherCorp";

    [Fact]
    public async Task MembershipAgeUnlocksEverMarkDonationsAtTheDeadlineAndDepartureRevokesThem()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        using (CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO NpcCorporation VALUES (1000419,'Paragon')");
        }
        var setup = Admit(world, clock);
        var billing = world.Services.GetRequiredService<IAllianceBilling>();
        var bill = Assert.Single(billing.Bills(Ceo));
        const int master = CorporationWalletRules.MasterKey;
        world
            .Services.GetRequiredService<ICorporationWallet>()
            .Transfer(Ceo, new(false, master, setup.Founder, master, bill.AmountCents, "Maintenance"));
        billing.Pay(Ceo, bill.Id, master);
        var live = world.Services.GetRequiredService<LiveWorld>();
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(
                new SavedLoyaltyBalance
                {
                    PilotId = Other,
                    IssuerCorporationId = Issuer,
                    Points = 100,
                }
            );
            action.Add(
                new SavedLoyaltyBalance
                {
                    PilotId = Ceo,
                    IssuerCorporationId = Issuer,
                    Points = 1,
                }
            );
            action.Commit();
        }
        using (CorporationTestClient founder = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            object? Age(object? corporation) => Reply(member, "allianceRegistry", "GetDaysInAlliance", [(long)setup.Alliance, corporation]);
            long[] Eligible(long days) =>
                Assert
                    .IsType<List<object?>>(Reply(founder, "allianceRegistry", "GetAllianceMembersOlderThan", [(long)setup.Alliance, days]))
                    .Cast<long>()
                    .ToArray();
            Assert.Equal(1L, Age((long)setup.Founder));
            Assert.Equal(0L, Age((long)setup.Member));
            Assert.Equal(0L, Age(null));
            Assert.Equal(new long[] { setup.Founder, setup.Member }, Eligible(0));
            Assert.Empty(Eligible(int.MaxValue));
            Assert.IsType<PilotCallResult.Failure>(member.CallService("allianceRegistry", "GetDaysInAlliance", [(long)setup.Alliance]));
            Assert.IsType<PilotCallResult.Failure>(
                member.CallService("allianceRegistry", "GetAllianceMembersOlderThan", [(long)setup.Alliance, -1L])
            );

            clock.AdvanceWallTime(CorporationLoyaltyRules.EverMarkMembership - TimeSpan.FromTicks(1));
            Assert.Equal(29L, Age((long)setup.Member));
            Assert.Equal(new long[] { setup.Founder }, Eligible(30));
            Assert.IsType<PilotCallResult.Failure>(member.CallService("LPSvc", FromPilot, [(long)setup.Founder, (long)Issuer, 25L]));
            clock.AdvanceWallTime(TimeSpan.FromTicks(1));
            Assert.Equal(30L, Age((long)setup.Member));
            Assert.Equal(new long[] { setup.Founder, setup.Member }, Eligible(30));
            Assert.Null(Reply(member, "LPSvc", FromPilot, [(long)setup.Founder, (long)Issuer, 25L]));
            Assert.Null(Reply(member, "LPSvc", FromPilot, [(long)setup.Member, (long)Issuer, 30L]));
            Assert.Null(Reply(member, "LPSvc", FromCorporation, [(long)setup.Founder, (long)Issuer, 12L]));
            Assert.Null(Reply(founder, "LPSvc", FromCorporation, [(long)setup.Member, (long)Issuer, 9L]));
            Assert.IsType<PilotCallResult.Failure>(founder.CallService("LPSvc", FromPilot, [(long)setup.Member, (long)Issuer, 1L]));

            var membership = world.Services.GetRequiredService<IAllianceMembership>();
            membership.Remove(Other, setup.Member);
            Assert.Equal(0L, Age((long)setup.Member));
            Assert.Equal(new long[] { setup.Founder }, Eligible(0));
            Assert.IsType<PilotCallResult.Failure>(member.CallService("LPSvc", FromPilot, [(long)setup.Founder, (long)Issuer, 1L]));
            Assert.IsType<PilotCallResult.Failure>(founder.CallService("LPSvc", FromCorporation, [(long)setup.Member, (long)Issuer, 1L]));
            membership.Apply(Other, setup.Alliance, "Rejoin");
            membership.Decide(Ceo, setup.Member, "Rejoin", AllianceApplicationStatus.Accepted);
            clock.AdvanceWallTime(AllianceMembershipRules.AdmissionDelay);
            membership.Advance();
            Assert.Equal(0L, Age((long)setup.Member));
            Assert.Equal(new long[] { setup.Founder }, Eligible(30));
            Assert.IsType<PilotCallResult.Failure>(member.CallService("LPSvc", FromCorporation, [(long)setup.Founder, (long)Issuer, 1L]));
            var alliances = world.Services.GetRequiredService<IAlliances>();
            using var metrics = SessionPerformance.Begin("alliance wallet eligibility", _ => { }, force: true)!;
            Assert.Equal(32, alliances.DaysInAlliance(setup.Alliance, setup.Founder));
            Assert.Equal(new[] { setup.Founder, setup.Member }, alliances.MembersOlderThan(setup.Alliance, 0));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
        }

        await live.FlushAsync();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var wallets = reopened.GetRequiredService<ICorporationLoyaltyWallet>();
        Assert.Equal(28L, Assert.Single(wallets.Read(Ceo)).Points);
        Assert.Equal(27L, Assert.Single(wallets.Read(Other)).Points);
        Assert.Equal(
            45L,
            reopened.GetRequiredService<IPilotLoyaltyWallet>().Read(Other).Single(b => b.IssuerCorporationId == Issuer).Points
        );
    }
}
