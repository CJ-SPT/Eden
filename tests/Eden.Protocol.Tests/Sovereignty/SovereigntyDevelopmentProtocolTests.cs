using Eden.Gameplay.Sovereignty;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Sovereignty;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyDevelopmentProtocolTests
{
    [Fact]
    public void NativeIndicesCapitalCommandsAndCappedAdmDecayPublishCurrentViews()
    {
        using SovereigntyCaptureFixture fixture = new();
        SovereigntyDevelopmentTests.Claim(fixture);
        SovereigntyDevelopmentTests.AddClaim(fixture, 30003990);
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
        object? Reply(string service, string method, params object?[] args)
        {
            var result = Assert.IsType<PilotCallResult.Success>(router.Call(context, service, method, args, _ => { }));
            return BlueMarshal.Decode(BlueMarshal.Encode(result.Reply));
        }
        var capital = Assert.IsType<BlueInstance>(Reply("allianceRegistry", "GetCapitalSystemInfo"));
        Assert.Equal((long)SovereigntyCaptureFixture.SystemId, BlueRead.Fields(capital.State)["currentCapitalSystem"]);
        Assert.Null(Reply("allianceRegistry", "SetCapitalSystem", 30003990L));
        capital = Assert.IsType<BlueInstance>(Reply("allianceRegistry", "GetCapitalSystemInfo"));
        Assert.Equal(30003990L, BlueRead.Fields(capital.State)["newCapitalSystem"]);
        Assert.Equal(fixture.Clock.Now + TimeSpan.FromDays(7).Ticks, BlueRead.Fields(capital.State)["newCapitalSystemValidAfter"]);
        Assert.Null(Reply("allianceRegistry", "CancelCapitalSystemTransition"));
        Assert.Contains(context.Notifications, row => row is SovereigntyCapitalInfoUpdated);
        SovereigntyDevelopmentTests.Queue(fixture, SovereigntyActivityKind.Military, 1e9);
        SovereigntyDevelopmentTests.Queue(fixture, SovereigntyActivityKind.Industrial, 1e9);
        var indices = Assert.IsType<Dictionary<long, object?>>(
            Reply("devIndexManager", "GetDevelopmentIndicesForSystem", (long)SovereigntyCaptureFixture.SystemId)
        );
        foreach (long attribute in new[] { 1583L, 1584L })
        {
            var index = Assert.IsType<BlueInstance>(indices[attribute]);
            Assert.Equal((long)SovereigntyDevelopmentRules.MaximumPoints, BlueRead.Fields(index.State)["points"]);
            Assert.Equal(true, BlueRead.Fields(index.State)["increasing"]);
        }
        var all = Assert.IsType<List<object?>>(Reply("devIndexManager", "GetAllDevelopmentIndices"));
        var own = all.Cast<BlueInstance>()
            .Single(row => Equals((long)SovereigntyCaptureFixture.SystemId, BlueRead.Fields(row.State)["solarSystemID"]));
        Assert.Equal(0L, BlueRead.Fields(own.State)["claimedFor"]);
        Assert.Equal((long)SovereigntyDevelopmentRules.MaximumPoints, BlueRead.Fields(own.State)["militaryPoints"]);
        var monitor = scope.ServiceProvider.GetRequiredService<SovereigntyMonitor>();
        monitor.Advance(context);
        context.Notifications.Clear();
        fixture.Clock.AdvanceWallTime(TimeSpan.FromDays(4) + TimeSpan.FromTicks(1));
        monitor.Advance(context);
        var development = fixture.Entosis.Development(SovereigntyCaptureFixture.SystemId);
        Assert.Equal(4, development.Indices.Military);
        Assert.Equal(4, development.Indices.Industrial);
        Assert.Equal(6m, development.DefenseMultiplier);
        Assert.Contains(context.Notifications, row => row is SovereigntyDevelopmentChanged);
        var structure = Assert.Single(
            context.Notifications.OfType<SovereigntyStructuresUpdated>(),
            row => row.SystemId == SovereigntyCaptureFixture.SystemId
        );
        var wire = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(new SovereigntyStructureReply(structure.Hub!))));
        Assert.Equal(6d, BlueRead.Fields(wire.State)["defenseMultiplier"]);
        Assert.Equal(true, BlueRead.Fields(wire.State)["isCapital"]);
        context.Notifications.Clear();
        monitor.Advance(context);
        Assert.Empty(context.Notifications);
        Assert.Null(Reply("allianceRegistry", "SetCapitalSystem", 30003990L));
        context.Notifications.Clear();
        fixture.Clock.AdvanceWallTime(TimeSpan.FromDays(7));
        monitor.Advance(context);
        var changed = Assert.Single(context.Notifications.OfType<SovereigntyCapitalChanged>());
        var arguments = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(changed.Arguments)), 1);
        Assert.Equal(
            new object?[] { (long)fixture.AllianceId, (long)SovereigntyCaptureFixture.SystemId, 30003990L },
            BlueRead.Tuple(arguments[0], 3)
        );
    }
}
