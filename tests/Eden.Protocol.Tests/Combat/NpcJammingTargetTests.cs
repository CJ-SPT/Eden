using Eden.Data.Combat;
using Eden.Data.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class NpcEwarTests
{
    private static PilotCallContext TargetContext(Fixture fixture)
    {
        var combat = fixture.Combat;
        PilotCallContext context = new(
            1000001,
            combat.Pilot.Account,
            [combat.Pilot],
            p => new(combat.Db.Store, p, combat.Clock, new(), [], encounters: combat.Db.Services.GetRequiredService<INpcEncounters>()),
            42,
            SignedSettings.Empty
        );
        context.Select(combat.Pilot);
        context.Flight.Snapshot();
        return context;
    }

    private static long AddNearbyTarget(Fixture fixture)
    {
        using WorldDbContext world = new(fixture.Combat.Db.Path);
        long id = world.Set<WorldNpc>().Max(n => n.Id) + 1;
        var original = world.Set<WorldNpc>().Find(fixture.Combat.Npc.Id)!;
        world.Add(
            new WorldNpc
            {
                Id = id,
                BeltId = original.BeltId,
                SystemId = original.SystemId,
                TypeId = NpcEncounterDefinitions.GuristasRookieType,
                X = original.X + 500,
                Y = original.Y,
                Z = original.Z,
                ShieldCharge = 20,
                UpdatedAt = fixture.Now,
            }
        );
        world.SaveChanges();
        return id;
    }

    private static TargetLockReply BeginScan(TargetingHandler targeting, PilotCallContext context, long target) =>
        Assert.IsType<TargetLockReply>(
            Assert.IsType<PilotCallResult.Success>(targeting.Call(context, "dogmaIM", "AddTarget", [target])).Reply
        );

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulJamCancelsCompletedAndPendingNonJammerLocksAndAllowsFreshRecovery(bool resident)
    {
        using Fixture f = new(2385, resident);
        long locked = AddNearbyTarget(f);
        long pending = AddNearbyTarget(f);
        using var context = TargetContext(f);
        using TargetingHandler targeting = new(f.Combat.Clock, f.Fitting, new(), f.Warfare);
        var completedScan = BeginScan(targeting, context, locked);
        Assert.False(completedScan.Completion!.IsCompleted);
        f.Combat.Clock.Seconds = 20;
        targeting.Advance(context);
        Assert.IsType<PilotCallResult.Success>(await completedScan.Completion);
        Assert.True(targeting.IsLocked(locked));
        var cancelledScan = BeginScan(targeting, context, pending);
        Assert.False(cancelledScan.Completion!.IsCompleted);
        context.Notifications.Clear();
        Assert.Single(f.Advance());
        targeting.Advance(context);
        Assert.False(targeting.IsLocked(locked));
        Assert.False(targeting.IsLocked(pending));
        Assert.Equal("TargetingAttemptCancelled", Assert.IsType<PilotCallResult.NamedFailure>(await cancelledScan.Completion).Name);
        Assert.Equal(
            new[] { locked, pending },
            context.Notifications.OfType<TargetChanged>().Where(n => n.Change == TargetChange.Lost).Select(n => n.TargetId).Order()
        );
        Assert.IsType<PilotCallResult.Failure>(targeting.Call(context, "dogmaIM", "AddTarget", [pending]));
        context.Notifications.Clear();
        targeting.Advance(context);
        Assert.Empty(context.Notifications);
        // A failed next cycle ends the jam, but cannot complete the cancelled scan.
        f.Roll.Value = 0.999999;
        f.Combat.Clock.Seconds = 40;
        Assert.Empty(f.Advance());
        targeting.Advance(context);
        Assert.False(targeting.IsLocked(pending));
        var retry = BeginScan(targeting, context, pending);
        Assert.NotSame(cancelledScan.Completion, retry.Completion);
        Assert.Same(retry.Completion, BeginScan(targeting, context, pending).Completion);
        Assert.False(retry.Completion!.IsCompleted);
        f.Combat.Clock.Seconds = 60;
        targeting.Advance(context);
        Assert.True(targeting.IsLocked(pending));
        Assert.IsType<PilotCallResult.Success>(await retry.Completion);
        Assert.Single(context.Notifications.OfType<TargetChanged>(), n => n.Change == TargetChange.Added && n.TargetId == pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulJamKeepsTheJammerLockAndClearsEveryOtherEstablishedLock(bool resident)
    {
        using Fixture f = new(2385, resident);
        long other = AddNearbyTarget(f);
        using var context = TargetContext(f);
        using TargetingHandler targeting = new(f.Combat.Clock, f.Fitting, new(), f.Warfare);
        var jammer = BeginScan(targeting, context, f.Combat.Npc.Id);
        var unrelated = BeginScan(targeting, context, other);
        f.Combat.Clock.Seconds = 20;
        targeting.Advance(context);
        Assert.IsType<PilotCallResult.Success>(await jammer.Completion!);
        Assert.IsType<PilotCallResult.Success>(await unrelated.Completion!);
        Assert.Single(f.Advance());
        targeting.Advance(context);
        Assert.True(targeting.IsLocked(f.Combat.Npc.Id));
        Assert.False(targeting.IsLocked(other));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleJammersRemainTargetableUntilTheirOwnSuccessfulCycleEnds(bool resident)
    {
        using Fixture f = new(2385, resident);
        long second = AddNearbyTarget(f);
        using (WorldDbContext world = new(f.Combat.Db.Path))
        {
            world.Set<WorldNpc>().Find(second)!.TypeId = 2385;
            world.Add(NpcAggression.Start(second, NpcCombatTests.Fixture.ShipId, 0, f.Now));
            world.SaveChanges();
        }
        using var context = TargetContext(f);
        using TargetingHandler targeting = new(f.Combat.Clock, f.Fitting, new(), f.Warfare);
        var firstScan = BeginScan(targeting, context, f.Combat.Npc.Id);
        var secondScan = BeginScan(targeting, context, second);
        f.Combat.Clock.Seconds = 20;
        targeting.Advance(context);
        Assert.IsType<PilotCallResult.Success>(await firstScan.Completion!);
        Assert.IsType<PilotCallResult.Success>(await secondScan.Completion!);
        Assert.Equal(2, f.Advance().Count);
        targeting.Advance(context);
        Assert.True(targeting.IsLocked(f.Combat.Npc.Id));
        Assert.True(targeting.IsLocked(second));
        f.Roll.Values.Enqueue(0);
        f.Roll.Values.Enqueue(0.999999);
        f.Combat.Clock.Seconds = 40;
        Assert.Equal(f.Combat.Npc.Id, Assert.Single(f.Advance()).SourceId);
        targeting.Advance(context);
        Assert.True(targeting.IsLocked(f.Combat.Npc.Id));
        Assert.False(targeting.IsLocked(second));
    }

    [Theory]
    [InlineData("retire")]
    [InlineData("cancel")]
    [InlineData("reset")]
    [InlineData("hull")]
    public async Task InterruptedScansAlwaysCompleteWithNativeCancellation(string reason)
    {
        using Fixture f = new(2385);
        using var context = TargetContext(f);
        using TargetingHandler targeting = new(f.Combat.Clock, f.Fitting, new(), f.Warfare);
        var scan = BeginScan(targeting, context, f.Combat.Npc.Id);
        switch (reason)
        {
            case "retire":
                using (WorldDbContext world = new(f.Combat.Db.Path))
                {
                    world.Set<WorldNpc>().Find(f.Combat.Npc.Id)!.RetiredAt = f.Now;
                    world.SaveChanges();
                }
                context.Flight.RefreshNpcs();
                targeting.Advance(context);
                break;
            case "cancel":
                targeting.Call(context, "dogmaIM", "CancelAddTarget", [f.Combat.Npc.Id]);
                break;
            case "hull":
                context.ReplaceDestroyedShip(context.Pilot with { ShipId = context.Pilot.ShipId + 1 });
                targeting.Advance(context);
                break;
            default:
                targeting.Reset();
                break;
        }
        Assert.True(scan.Completion!.IsCompleted);
        Assert.Equal("TargetingAttemptCancelled", Assert.IsType<PilotCallResult.NamedFailure>(await scan.Completion).Name);
        f.Combat.Clock.Seconds = 100;
        targeting.Advance(context);
        Assert.False(targeting.IsLocked(f.Combat.Npc.Id));
    }
}
