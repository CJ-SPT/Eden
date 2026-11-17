using Eden.Gameplay.Flight;
using Eden.Gameplay.Sovereignty;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Sovereignty;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyWarfareProtocolTests
{
    [Fact]
    public void DefenderOutsideTheSourceSystemReceivesOnlyChangedAllianceViews()
    {
        using SovereigntyCaptureFixture fixture = new();
        long defender = fixture.Fit();
        long attacker = fixture.Fit(pilot: SovereigntyCaptureFixture.OtherPilotId);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 500);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 500, pilot: SovereigntyCaptureFixture.OtherPilotId);
        long hub = SovereigntyCampaignTests.Claim(fixture, defender);
        fixture.Position(new(1_000_000, 0, 0), system: 30000142);
        var pilot = fixture.World.Store.FindPilot(1000001)!;
        using var scope = fixture.World.Services.CreateScope();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create(selected),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var monitor = scope.ServiceProvider.GetRequiredService<SovereigntyMonitor>();
        monitor.Advance(context);
        context.Notifications.Clear();
        SovereigntyCampaignTests.Reinforce(fixture, attacker, hub);
        monitor.Advance(context);
        var notice = Assert.Single(context.Notifications.OfType<SovereigntyStructuresUpdated>());
        Assert.Equal(SovereigntyCaptureFixture.SystemId, notice.SystemId);
        Assert.Equal(SovereigntyPhase.Reinforced, notice.Hub!.Conflict!.Phase);
        context.Notifications.Clear();
        monitor.Advance(context);
        Assert.Empty(context.Notifications);
    }

    [Fact]
    public void NativePrimeCampaignRowsAndNodeActivationPublishMatchingSourceIdentity()
    {
        using SovereigntyCaptureFixture fixture = new();
        long defender = fixture.Fit();
        long attacker = fixture.Fit(pilot: SovereigntyCaptureFixture.OtherPilotId);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 500);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 500, pilot: SovereigntyCaptureFixture.OtherPilotId);
        long hub = SovereigntyCampaignTests.Claim(fixture, defender);
        var pilot = fixture.World.Store.FindPilot(1000001)!;
        using var scope = fixture.World.Services.CreateScope();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create(selected),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        object? Reply(string service, string method, object?[] args)
        {
            var result = Assert.IsType<PilotCallResult.Success>(router.Call(context, service, method, args, _ => { }));
            return BlueMarshal.Decode(BlueMarshal.Encode(result.Reply));
        }
        Assert.Null(Reply("allianceRegistry", "SetPrimeHour", [0L]));
        var prime = Assert.IsType<BlueInstance>(Reply("allianceRegistry", "GetPrimeTimeInfo", []));
        Assert.Equal(18L, BlueRead.Fields(prime.State)["currentPrimeHour"]);
        Assert.Equal(0L, BlueRead.Fields(prime.State)["newPrimeHour"]);
        var monitor = scope.ServiceProvider.GetRequiredService<SovereigntyMonitor>();
        monitor.Advance(context);
        SovereigntyCampaignTests.Reinforce(fixture, attacker, hub);
        monitor.Advance(context);
        Assert.Contains(context.Notifications, row => row is SovereigntyStructuresUpdated);
        long at = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.CampaignAt!.Value;
        var rows = BlueRead.Tuple(Reply("allianceRegistry", "GetAllianceSovereigntyStructuresInfo", []), 3);
        Assert.Empty(Assert.IsType<List<object?>>(rows[0]));
        var owned = Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(rows[1])));
        Assert.Equal(hub, BlueRead.Fields(owned.State)["structureID"]);
        Assert.Equal(at, BlueRead.Fields(owned.State)["campaignStartTime"]);
        Assert.Equal(2, Assert.IsType<List<object?>>(rows[2]).Count);
        var info = Assert.IsType<BlueInstance>(
            Assert.Single(
                Assert.IsType<List<object?>>(
                    Reply("sovMgr", "GetSovStructuresInfoForSolarSystem", [(long)SovereigntyCaptureFixture.SystemId])
                )
            )
        );
        var campaign = BlueRead.Tuple(BlueRead.Fields(info.State)["campaignState"], 4);
        Assert.Equal(new object?[] { 2L, (long)fixture.AllianceId, at }, campaign[..3]);
        var scores = Assert.IsType<Dictionary<long, object?>>(campaign[3]);
        Assert.Equal(.6, scores[fixture.AllianceId]);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(at - fixture.Clock.Now));
        fixture.Entosis.AdvanceAll();
        var node = SovereigntyCampaignTests.Nodes(fixture).First(row => row.SystemId == SovereigntyCaptureFixture.SystemId);
        fixture.Position(new(node.X + 1000, node.Y, node.Z));
        var selected = fixture.World.Store.FindPilot(1000001)!;
        context.ChangeShip(selected);
        context.Flight.Snapshot();
        Assert.NotNull(context.Flight.FindTarget(node.Id));
        fixture.Lock(node.Id);
        var modules = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        Assert.Same(PilotCallResult.None, modules.Call(context, "dogmaIM", "Activate", [defender, "entosisLink", node.Id, 0L]));
        Assert.Contains(
            context.Flight.Updates,
            update => update.Command is DestinyCommand.TimedSpecialEffect { Graphic: "effects.EntosisBeam", Start: true }
        );
    }
}
