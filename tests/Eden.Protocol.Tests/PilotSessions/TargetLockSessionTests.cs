using Eden.Data.Catalog;
using Eden.Data.Combat.Npcs;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Targeting;
using Eden.Protocol.Tests.Combat;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class TargetLockSessionTests
{
    private sealed class ScanningHandler(Task<PilotCallResult> completion) : ISelectedPilotCallHandler
    {
        public PilotCallResult Call(
            PilotCallContext context,
            string service,
            string method,
            object?[] args,
            Dictionary<string, object?>? keywords = null
        ) => method == "AddTarget" ? new TargetLockReply(true, []) { Completion = completion } : new BlueList<long>([]);
    }

    private static PilotCallContext Context(TestWorld world)
    {
        var pilot = world.Store.FindPilot(1000001)!;
        PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(world.Store, p, TimeProvider.System, new(), []),
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        return context;
    }

    private static PilotSessionOutput Output(PilotCallContext context, List<BlueInstance> packets) =>
        new(
            context,
            1000001,
            42,
            packet =>
            {
                packets.Add(Assert.IsType<BlueInstance>(packet));
                return Task.CompletedTask;
            },
            _ => { }
        );

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingScanDoesNotReplyEarlyOrBlockOtherCallsAndCompletesExactlyOnce(bool success)
    {
        using TestWorld world = new();
        using var scope = world.Services.CreateScope();
        using var context = Context(world);
        TaskCompletionSource<PilotCallResult> scan = new();
        List<BlueInstance> packets = [];
        var output = Output(context, packets);
        PilotSessionCalls calls = new(
            context,
            output,
            new PilotCallRouter([], [new ScanningHandler(scan.Task)]),
            scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>(),
            scope.ServiceProvider.GetRequiredService<SkillHandler>(),
            TimeProvider.System,
            _ => { },
            1000001,
            42
        );
        var request = PilotServicesTests.Request("dogmaIM", "AddTarget", [10L]);
        await calls.ProcessAsync(request);
        Assert.DoesNotContain(packets, p => p.Name.EndsWith(".CallRsp", StringComparison.Ordinal));
        Assert.False(scan.Task.IsCompleted);
        packets.Clear();
        await calls.ProcessAsync(PilotServicesTests.Request("dogmaIM", "GetTargets", []));
        Assert.Single(packets, p => p.Name.EndsWith(".CallRsp", StringComparison.Ordinal));
        packets.Clear();
        PilotCallResult result = success
            ? new PilotCallResult.Success(new TargetLockReply(true, [10]))
            : new PilotCallResult.NamedFailure("TargetingAttemptCancelled", BlueEmpty.Dictionary);
        scan.SetResult(result);
        await output.FlushNotificationsAsync();
        var reply = Assert.Single(packets);
        var incoming = IncomingMachoPacket.Read(request);
        var expected = success
            ? incoming.CallSucceeded(((PilotCallResult.Success)result).Reply)
            : incoming.UserError((PilotCallResult.NamedFailure)result);
        Assert.Equal(BlueMarshal.Encode(expected), BlueMarshal.Encode(reply));
        await output.FlushNotificationsAsync();
        Assert.Single(packets);
    }

    [Fact]
    public async Task CancelledScanAndLostLocksReachTheClientBeforeNativeBracketRemoval()
    {
        using TestWorld world = new();
        var pilot = world.Store.FindPilot(1000001)!;
        world.Store.SetPosition(pilot.ShipId, pilot.Home.Position + new SpacePosition(1_000_000, 0, 0));
        using var context = Context(world);
        context.Flight.Snapshot();
        context.Flight.Updates.Clear();
        List<BlueInstance> packets = [];
        var output = Output(context, packets);
        TaskCompletionSource<PilotCallResult> scan = new();
        output.DeferTargetLockReply(IncomingMachoPacket.Read(PilotServicesTests.Request("dogmaIM", "AddTarget", [10L])), scan.Task);
        scan.SetResult(new PilotCallResult.NamedFailure("TargetingAttemptCancelled", BlueEmpty.Dictionary));
        context.Notifications.Add(new TargetChanged(TargetChange.Lost, 10));
        context.Notifications.Add(new TargetChanged(TargetChange.OtherLost, 11));
        context.Flight.Updates.Add(new(context.Flight.CurrentStamp, new DestinyCommand.RemoveBall(10, false)));
        await output.FlushFlightUpdatesAsync();
        Assert.Equal(4, packets.Count);
        Assert.EndsWith(".ErrorResponse", packets[0].Name);
        Assert.Equal(new[] { "OnTarget", "OnTarget", "DoDestinyUpdate" }, packets.Skip(1).Select(NotificationName));
        Assert.Empty(context.Notifications);
        Assert.Empty(context.Flight.Updates);
        await output.FlushFlightUpdatesAsync();
        Assert.Equal(4, packets.Count);
    }

    private static string NotificationName(BlueInstance packet)
    {
        var destination = Assert.IsType<BlueInstance>(BlueRead.Tuple(packet.State, 14)[2]);
        return Assert.IsType<string>(BlueRead.Tuple(destination.State, 4)[1]);
    }

    [Fact]
    public async Task RetirementTickCancelsThePendingRequestBeforePublishingDeparture()
    {
        using NpcCombatTests.Fixture fixture = new(useTestClock: true);
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath, writable: true))
        {
            foreach (
                int belt in catalog
                    .Set<Celestial>()
                    .Where(c => c.SolarSystemId == fixture.Npc.SystemId && c.GroupId == AsteroidDefinitions.BeltGroup)
                    .Select(c => c.Id)
            )
            {
                string key = belt.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string counts = "{}";
                catalog.Database.ExecuteSql(
                    $"INSERT OR REPLACE INTO SourceRecord VALUES ('client/asteroidcountbytypeidbeltid',{key},{counts})"
                );
            }
        }
        using var scope = fixture.Db.Services.CreateScope();
        var provider = scope.ServiceProvider;
        using PilotCallContext context = new(
            1000001,
            fixture.Pilot.Account,
            [fixture.Pilot],
            provider.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        );
        context.Select(fixture.Pilot);
        var updates = provider.GetRequiredService<PilotUpdateCoordinator>();
        updates.Enter(context);
        context.Flight.Snapshot();
        context.Flight.Updates.Clear();
        List<BlueInstance> packets = [];
        var output = Output(context, packets);
        PilotSessionCalls calls = new(
            context,
            output,
            provider.GetRequiredService<PilotCallRouter>(),
            updates,
            provider.GetRequiredService<SkillHandler>(),
            fixture.Clock,
            _ => { },
            1000001,
            42
        );
        await calls.ProcessAsync(PilotServicesTests.Request("dogmaIM", "AddTarget", [fixture.Npc.Id]));
        Assert.DoesNotContain(packets, p => p.Name.EndsWith(".CallRsp", StringComparison.Ordinal));
        packets.Clear();
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Set<WorldNpc>().Find(fixture.Npc.Id)!.RetiredAt = fixture.Clock.Now;
            world.SaveChanges();
        }
        fixture.Clock.Seconds = 1;
        PilotSessionTick tick = new(
            fixture.Clock,
            updates,
            provider.GetRequiredService<SkillHandler>(),
            provider.GetRequiredService<IAsteroidField>()
        );
        await tick.AdvanceAsync(context, output, _ => { }, 1000001, 42);
        int cancelled = packets.FindIndex(p => p.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
        int departure = packets.FindIndex(p =>
            p.Name.EndsWith(".Notification", StringComparison.Ordinal) && NotificationName(p) == "DoDestinyUpdate"
        );
        Assert.True(cancelled >= 0 && departure > cancelled);
        Assert.DoesNotContain(context.Flight.VisibleNpcs, n => n.Id == fixture.Npc.Id);
        Assert.False(provider.GetRequiredService<TargetingHandler>().IsLocked(fixture.Npc.Id));
    }
}
