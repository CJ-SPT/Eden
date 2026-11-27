using Eden.Gameplay.Alliances;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Sovereignty;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Sovereignty.SovereigntyCaptureFixture;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyDissolutionNotificationTests
{
    private const int ObserverSystem = 30000142;

    [Fact]
    public void UnpaidClosureClearsPreviouslyObservedRemoteSovereigntyAfterAffiliationIsLost()
    {
        using SovereigntyCaptureFixture fixture = new();
        Claim(fixture);
        fixture.Position(new(1_000_000, 0, 0), system: ObserverSystem);
        using var scope = fixture.World.Services.CreateScope();
        using var context = Observe(fixture, scope.ServiceProvider, 1000001);
        var monitor = scope.ServiceProvider.GetRequiredService<SovereigntyMonitor>();
        monitor.Advance(context);
        Assert.Empty(context.Notifications);

        var billing = fixture.World.Services.GetRequiredService<IAllianceBilling>();
        long closesAt = AllianceBillingRules.NextDowntime(Assert.Single(billing.Bills(PilotId)).DueAt);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(closesAt - fixture.Clock.Now));
        fixture.World.Services.GetRequiredService<IAllianceMembership>().Advance();
        Assert.Null(fixture.World.Services.GetRequiredService<IAlliances>().Affiliation(fixture.CorporationId));
        Assert.Null(fixture.Claims.Read(SystemId));
        monitor.Advance(context);

        var status = Assert.Single(context.Notifications.OfType<SovereigntyStatusChanged>());
        Assert.Equal(new object?[] { (long)SystemId, null }, BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(status.Arguments)), 2));
        var structures = Assert.Single(context.Notifications.OfType<SovereigntyStructuresUpdated>());
        var fields = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(structures.Arguments)), 3);
        Assert.Equal((long)SystemId, fields[0]);
        Assert.Empty(Assert.IsType<List<object?>>(fields[1]));
        Assert.Null(fields[2]);
        Assert.Equal(SystemId, Assert.Single(context.Notifications.OfType<SovereigntyDevelopmentChanged>()).SystemId);

        context.Notifications.Clear();
        monitor.Advance(context);
        Assert.Empty(context.Notifications);
    }

    [Fact]
    public void OrdinaryDepartureKeepsRemoteSovereigntyWhenThePreviousAllianceRetainsItsClaim()
    {
        using SovereigntyCaptureFixture fixture = new();
        long hub = Claim(fixture);
        int corporation = fixture.World.Store.FindPilot(1000002)!.CorpId;
        var membership = fixture.World.Services.GetRequiredService<IAllianceMembership>();
        membership.Remove(OtherPilotId, corporation);
        membership.Apply(OtherPilotId, fixture.AllianceId, "Join");
        membership.Decide(PilotId, corporation, "Join", AllianceApplicationStatus.Accepted);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        membership.Advance();
        var alliances = fixture.World.Services.GetRequiredService<IAlliances>();
        Assert.Equal(fixture.AllianceId, alliances.Affiliation(corporation));

        using var scope = fixture.World.Services.CreateScope();
        using var context = Observe(fixture, scope.ServiceProvider, 1000002);
        var monitor = scope.ServiceProvider.GetRequiredService<SovereigntyMonitor>();
        monitor.Advance(context);
        long revision = fixture.Entosis.ViewRevision;
        fixture.Entosis.SetCapital(PilotId, SystemId);
        membership.Remove(OtherPilotId, corporation);
        Assert.Null(alliances.Affiliation(corporation));
        Assert.True(fixture.Entosis.ViewRevision > revision);
        Assert.Equal(hub, fixture.Claims.Read(SystemId)!.HubId);
        monitor.Advance(context);
        Assert.Empty(context.Notifications);
    }

    private static long Claim(SovereigntyCaptureFixture fixture)
    {
        long module = fixture.Fit();
        fixture.Grant(Strontium, 500);
        return SovereigntyCampaignTests.Claim(fixture, module);
    }

    private static PilotCallContext Observe(SovereigntyCaptureFixture fixture, IServiceProvider services, long accountId)
    {
        var pilot = fixture.World.Store.FindPilot(accountId)!;
        Assert.NotEqual(SystemId, pilot.SystemId);
        PilotCallContext context = new(
            accountId,
            pilot.Account,
            [pilot],
            selected => services.GetRequiredService<FlightSessionFactory>().Create(selected),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        return context;
    }
}
