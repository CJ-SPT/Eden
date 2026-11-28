using Eden.Data.World.Corporations;
using Eden.Data.World.Structures;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Structures;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Alliances.AllianceCreationTests;

namespace Eden.Protocol.Tests.Alliances;

public sealed class CorporationWarInheritanceTests
{
    [Fact]
    public async Task AcceptedAllianceApplicationSurvivesAnInterveningMutualWarAndJoinsAfterCooldown()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        Corporation(world);
        int applicant = Corporation(world, Other, "Applying Corporation", "APPC");
        long opponentCeo = AllianceMembershipTests.AdditionalPilot(world, "Opposing Director");
        int opponent = Corporation(world, opponentCeo, "Opposing Corporation", "OPPC");
        var alliances = world.Services.GetRequiredService<IAlliances>();
        int alliance = alliances.Create(Ceo, Details()).Alliance.Id;
        var membership = world.Services.GetRequiredService<IAllianceMembership>();
        membership.Apply(Other, alliance, "Request admission");
        membership.Decide(Ceo, applicant, "Accepted", AllianceApplicationStatus.Accepted);
        long acceptedCursor = membership.Head;

        var wars = world.Services.GetRequiredService<ICorporationWars>();
        wars.SendInvitation(opponentCeo, applicant);
        wars.RespondInvitation(Other, opponent, true);
        var war = Assert.Single(wars.Wars(applicant));
        clock.AdvanceWallTime(AllianceMembershipRules.AdmissionDelay);
        membership.Advance();
        Assert.Null(alliances.Affiliation(applicant));
        var deferred = Assert.Single(membership.Applications(Other, false));
        Assert.Equal(AllianceApplicationStatus.Accepted, deferred.Status);
        Assert.Equal(clock.Now + TimeSpan.TicksPerHour, deferred.EffectiveAt);
        Assert.Equal(acceptedCursor, membership.Head);

        wars.RetractMutual(Other, war.Id);
        await world.Services.GetRequiredService<LiveWorld>().FlushAsync();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var saved = reopened.GetRequiredService<IAllianceMembership>();
        Assert.Equal(deferred, Assert.Single(saved.Applications(Other, false)));
        clock.AdvanceWallTime(TimeSpan.FromTicks(CorporationWarRules.Cooldown - 1));
        saved.Advance();
        Assert.Null(reopened.GetRequiredService<IAlliances>().Affiliation(applicant));
        var retry = Assert.Single(saved.Applications(Other, false));
        clock.AdvanceWallTime(TimeSpan.FromTicks(retry.EffectiveAt!.Value - clock.Now));
        saved.Advance();
        Assert.Equal(alliance, reopened.GetRequiredService<IAlliances>().Affiliation(applicant));
        var joined = Assert.Single(saved.Changes(Other, acceptedCursor), change => change.MemberAfter is not null);
        Assert.Equal(clock.Now, joined.MemberAfter!.JoinedAt);
    }

    [Fact]
    public async Task DefensiveAllianceDepartureCopiesDatesAndIndependentBillOnce()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        int departing = Corporation(world);
        int attacker = Corporation(world, Other, "Attacking Corporation", "ATCK");
        int alliance = world.Services.GetRequiredService<IAlliances>().Create(Ceo, Details()).Alliance.Id;
        var live = world.Services.GetRequiredService<LiveWorld>();
        const long headquarters = 1_030_000_000_001;
        long originalId = 500;
        long declared = clock.Now - TimeSpan.TicksPerDay * 2;
        long starts = declared + CorporationWarRules.Warmup;
        long due = declared + CorporationWarRules.BillingPeriod;
        long originalBill;
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            originalBill = action.Next("WorldAllianceBill");
            action.Add(
                new SavedCorporationWar
                {
                    Id = originalId,
                    AttackerId = attacker,
                    DefenderId = alliance,
                    HeadquartersId = headquarters,
                    DeclaredAt = declared,
                    StartsAt = starts,
                    BillId = originalBill,
                    BillIssuedAt = declared,
                    BillDueAt = due,
                }
            );
            action.Add(
                new SavedStructure
                {
                    Id = headquarters,
                    CorporationId = attacker,
                    TypeId = 35832,
                    State = StructureState.ShieldVulnerable,
                }
            );
            action.Add(
                new SavedStructure
                {
                    Id = headquarters + 1,
                    CorporationId = departing,
                    TypeId = 35832,
                    State = StructureState.ShieldVulnerable,
                }
            );
            action.Commit();
        }

        world.Services.GetRequiredService<IAllianceMembership>().Remove(Ceo, departing);
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            CorporationWarStore.InheritDepartingWars(action, alliance, departing, clock.Now);
            Assert.True(CorporationWarStore.HasActiveWar(action, attacker, departing, clock.Now));
            action.Commit();
        }

        var wars = world.Services.GetRequiredService<ICorporationWars>();
        var inherited = Assert.Single(wars.Wars(departing));
        Assert.Equal(
            (originalId, declared, starts, (long?)headquarters),
            (inherited.InheritedFromWarId.GetValueOrDefault(), inherited.DeclaredAt, inherited.StartsAt, inherited.HeadquartersId)
        );
        Assert.Null(inherited.EndsAt);
        Assert.NotEqual(originalBill, inherited.BillId);
        var bill = Assert.Single(
            world.Services.GetRequiredService<ICorporationBilling>().WarInvoices(Other),
            invoice => invoice.WarId == inherited.Id
        );
        Assert.Equal((due, inherited.BillId), (bill.DueAt, (long?)bill.Id));
        Assert.Equal(alliance, wars.War(originalId)!.DefenderId);

        await live.FlushAsync();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        Assert.Equal(inherited, Assert.Single(reopened.GetRequiredService<ICorporationWars>().Wars(departing)));
    }
}
