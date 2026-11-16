using Eden.Gameplay.Flight;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Sovereignty;
using Eden.Protocol.Targeting;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyProtocolTests
{
    [Fact]
    public void NativeDeploymentTargetingAndEntosisPublishEffectsAndRetainPaidRestrictions()
    {
        using SovereigntyCaptureFixture fixture = new();
        long module = fixture.Fit();
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 10);
        long stack = fixture.Grant(EntosisRules.HubTypeId);
        // Remain outside the hub's 45 km collision sphere and inside link range.
        fixture.Position(new(2_000_070_000, 0, 0));
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
        context.Flight.Snapshot();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        object?[] launchArgs = [new List<object?> { new object?[] { stack, 1L } }, SovereigntyCaptureFixture.PilotId, false];
        var launch = Assert.IsType<PilotCallResult.Success>(
            router.Call(context, "ship", "LaunchDrones", BlueMarshal.Decode(BlueMarshal.Encode(launchArgs)), _ => { })
        );
        var deployed = Assert.IsType<Dictionary<long, object?>>(BlueMarshal.Decode(BlueMarshal.Encode(launch.Reply)));
        long hub = Assert.IsType<long>(Assert.Single(Assert.IsType<List<object?>>(deployed[stack])));
        Assert.Equal(0, fixture.Quantity(stack));
        Assert.NotNull(context.Flight.FindTarget(hub));
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        Assert.IsType<PilotCallResult.Success>(targeting.Call(context, "dogmaIM", "AddTarget", [hub]));
        fixture.Clock.AdvanceElapsed(TimeSpan.FromSeconds(60));
        targeting.Advance(context);
        Assert.True(targeting.IsLocked(hub));
        context.Flight.SetBaseSpeed(9000);
        var modules = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        Assert.Same(PilotCallResult.None, modules.Call(context, "dogmaIM", "Activate", [module, "entosisLink", hub, 0L]));
        Assert.Equal(4000, context.Pilot.Flight.MaxSpeed);
        Assert.Contains(
            context.Flight.Updates,
            update => update.Command is DestinyCommand.TimedSpecialEffect { Graphic: "effects.EntosisBeam", Start: true }
        );
        Assert.Contains(context.Notifications, notification => notification is ShipEffectChanged { Start: true });
        Assert.IsType<PilotCallResult.Failure>(context.Flight.Execute(new FlightCommand.WarpToPoint(1, new(4_000_000_000, 0, 0), 0)));
        Assert.IsType<PilotCallResult.Failure>(
            context.Flight.StartJump(new(1, 2, fixture.Ship()), fixture.World.Services.GetRequiredService<ISystemObjects>())
        );
        Assert.IsType<PilotCallResult.Failure>(context.Flight.Execute(new FlightCommand.Dock(60003760, fixture.Ship())));
        var cloak = fixture.World.Services.GetRequiredService<IPilotSpatialModules>();
        Assert.Throws<InvalidDataException>(() =>
            cloak.Activate(
                SovereigntyCaptureFixture.PilotId,
                module,
                Eden.Gameplay.Inventory.DogmaEffect.Cloaking,
                new(1, 0, 0),
                SpacePosition.Zero,
                fixture.Clock.Now
            )
        );
        Assert.Same(PilotCallResult.None, modules.Call(context, "dogmaIM", "Deactivate", [module, "entosisLink"]));
        var handler = scope.ServiceProvider.GetRequiredService<EntosisHandler>();
        Assert.NotNull(handler.ActiveEffect(pilot, module));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(pilot, module));
        Assert.Equal(4000, context.Pilot.Flight.MaxSpeed);
        Assert.False(fixture.Entosis.TravelLocked(fixture.Ship(), fixture.Clock.Now));
        Assert.Null(fixture.Claims.Read(SovereigntyCaptureFixture.SystemId));
    }

    [Fact]
    public void ClaimedHubRepliesMapRowsSlimScoreAndNotificationsUseNativeShapes()
    {
        using SovereigntyCaptureFixture fixture = new();
        long module = fixture.Fit();
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 10);
        long hub = fixture.Deploy();
        fixture.Lock(hub);
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
        context.Flight.Snapshot();
        var monitor = scope.ServiceProvider.GetRequiredService<SovereigntyMonitor>();
        monitor.Advance(context);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        object? Reply(string service, string method, object?[] args)
        {
            var result = Assert.IsType<PilotCallResult.Success>(router.Call(context, service, method, args, _ => { }));
            return BlueMarshal.Decode(BlueMarshal.Encode(result.Reply));
        }
        Assert.Null(Reply("sovMgr", "GetInfrastructureHubInfo", [(long)SovereigntyCaptureFixture.SystemId]));
        fixture.Entosis.Activate(SovereigntyCaptureFixture.PilotId, fixture.Ship(), module, hub, 10, fixture.Clock.Now);
        for (int cycle = 0; cycle < 6; cycle++)
        {
            fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
            fixture.Entosis.Renew(SovereigntyCaptureFixture.PilotId, module, fixture.Clock.Now);
        }
        var claim = fixture.Claims.Read(SovereigntyCaptureFixture.SystemId)!;
        var hubInfo = Assert.IsType<BlueConstructor>(
            Reply("sovMgr", "GetInfrastructureHubInfo", [(long)SovereigntyCaptureFixture.SystemId])
        );
        Assert.Equal("sovereignty.data_types.SovHubInfo", hubInfo.Name);
        Assert.Equal(new object?[] { hub, (long)fixture.CorporationId, (long)fixture.AllianceId, claim.ClaimedAt }, hubInfo.Arguments);
        var claimInfo = Assert.IsType<BlueConstructor>(
            Reply("sovMgr", "GetSystemSovereigntyInfo", [(long)SovereigntyCaptureFixture.SystemId])
        );
        Assert.Equal("sovereignty.data_types.SovClaimInfo", claimInfo.Name);
        Assert.Equal(3, claimInfo.Arguments.Length);
        var mapRow = Assert.IsType<BlueInstance>(
            Assert.Single(Assert.IsType<List<object?>>(Reply("stationSvc", "GetAllianceSystems", [])))
        );
        Assert.Equal((long)fixture.AllianceId, BlueRead.Fields(mapRow.State)["allianceID"]);
        Assert.Equal((long)SovereigntyCaptureFixture.SystemId, BlueRead.Fields(mapRow.State)["solarSystemID"]);
        var contacts = fixture.World.Services.GetRequiredService<IStructureContacts>().Contacts(SovereigntyCaptureFixture.SystemId);
        var body = Assert.Single(contacts, body => body.Id == hub);
        var slim = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(new StaticSlimItem(body))));
        var fields = BlueRead.Fields(slim.State);
        Assert.Equal((long)fixture.CorporationId, fields["corpID"]);
        Assert.Equal((long)fixture.AllianceId, fields["allianceID"]);
        var score = BlueRead.Tuple(fields["entosis_score"], 4);
        Assert.Equal(1d, BlueRead.Tuple(score[2], 4)[0]);
        Assert.Equal((long)fixture.AllianceId, score[3]);
        context.Flight.RefreshStructures(contacts);
        context.Flight.RefreshStructures(contacts.ToArray());
        Assert.NotNull(context.Flight.FindTarget(hub));
        monitor.Advance(context);
        Assert.Contains(context.Notifications, notification => notification is SovereigntyStructuresUpdated);
        var status = Assert.Single(context.Notifications.OfType<SovereigntyStatusChanged>());
        var args = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(status.Arguments)), 2);
        Assert.IsType<BlueConstructor>(args[1]);
    }

    [Theory]
    [InlineData("sovereignty.data_types.SovHubInfo", 3)]
    [InlineData("sovereignty.data_types.SovClaimInfo", 4)]
    [InlineData("unknown.SovHubInfo", 4)]
    public void SovereigntyTupleDecoderRejectsUnknownClassesAndInvalidArities(string name, int count) =>
        Assert.Throws<InvalidDataException>(() => BlueMarshal.Decode(BlueMarshal.Encode(new BlueConstructor(name, new object?[count]))));
}
