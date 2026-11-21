using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Structures;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Structures;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureTargetingTests
{
    private const long TargetId = 59000001;

    [Fact]
    public void PendingScansUseTheHullTargetCapacityAndCancellationFreesCapacity()
    {
        using var f = StructureAttackTests.Prepare();
        var structure = Prepare(f);
        using (var action = StructureAttackTests.Scope(f))
        {
            for (int index = 1; index <= 7; index++)
            {
                action.World.Add(
                    new WorldNpc
                    {
                        Id = TargetId + index,
                        TypeId = 601,
                        SystemId = Fixture.SystemId,
                        X = structure.Position.X + structure.Profile.Radius + 1000,
                        UpdatedAt = f.Clock.Now,
                    }
                );
            }
            action.Commit();
        }
        using var lease = f.Db.Services.GetRequiredService<IPilotStructureControl>().Acquire(structure.Id, f.PilotId, 101);
        var targeting = f.Db.Services.GetRequiredService<IPilotStructureTargeting>();
        for (int index = 0; index < 7; index++)
        {
            Assert.Equal(index + 1, targeting.Add(lease.Token, TargetId + index).Targets.Count);
        }
        Assert.Throws<InvalidDataException>(() => targeting.Add(lease.Token, TargetId + 7));
        Assert.All(targeting.Read(lease.Token).Targets, target => Assert.False(target.Locked));
        Assert.Equal(6, targeting.Remove(lease.Token, TargetId).Targets.Count);
        Assert.Equal(7, targeting.Add(lease.Token, TargetId + 7).Targets.Count);
        Assert.Empty(targeting.Clear(lease.Token).Targets);
    }

    [Fact]
    public void ResidentAcquisitionUsesTheStructureAndReleaseClearsOnlyOutgoingLocks()
    {
        using var f = StructureAttackTests.Prepare();
        var structure = Prepare(f);
        using var lease = f.Db.Services.GetRequiredService<IPilotStructureControl>().Acquire(structure.Id, f.PilotId, 101);
        var targeting = f.Db.Services.GetRequiredService<IPilotStructureTargeting>();
        var locks = f.Db.Services.GetRequiredService<SharedTargetLocks>();
        var pending = Assert.Single(targeting.Add(lease.Token, TargetId).Targets);
        Assert.False(pending.Locked);
        Assert.True(pending.ReadyAt > f.Clock.Now);
        Assert.True(locks.HasOutgoing(structure.Id));
        Assert.False(locks.HasOutgoing(f.ShipId));
        Assert.Throws<InvalidDataException>(() => targeting.RequireLocked(lease.Token, TargetId));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(pending.ReadyAt - f.Clock.Now));
        Assert.True(Assert.Single(targeting.Read(lease.Token).Targets).Locked);
        targeting.RequireLocked(lease.Token, TargetId);
        Assert.True(locks.Contains(structure.Id, TargetId));
        using (var metrics = SessionPerformance.Begin("warm structure targets", _ => { }, force: true)!)
        {
            Assert.True(Assert.Single(targeting.Read(lease.Token).Targets).Locked);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        locks.Add(TargetId, structure.Id);
        lease.Dispose();
        Assert.False(locks.Contains(structure.Id, TargetId));
        Assert.True(locks.Contains(TargetId, structure.Id));
        Assert.Throws<InvalidDataException>(() => targeting.Read(lease.Token));
        using var replacement = f.Db.Services.GetRequiredService<IPilotStructureControl>().Acquire(structure.Id, f.PilotId, 102);
        Assert.Empty(targeting.Read(replacement.Token).Targets);
        Assert.Equal(TargetId, Assert.Single(targeting.Read(replacement.Token).Targeters));
        Assert.Null(f.Db.Store.FindPilot(1000001)!.Position);
        Assert.Equal(f.ShipId, f.Db.Store.FindPilot(1000001)!.ShipId);
    }

    [Fact]
    public void RangeDepartureAndReinforcementCancelPendingAcquisitionWithoutReplayingIt()
    {
        using var f = StructureAttackTests.Prepare();
        var structure = Prepare(f);
        using var lease = f.Db.Services.GetRequiredService<IPilotStructureControl>().Acquire(structure.Id, f.PilotId, 101);
        var targeting = f.Db.Services.GetRequiredService<IPilotStructureTargeting>();
        Assert.Throws<InvalidDataException>(() => targeting.Add(lease.Token, structure.Id));
        var scan = Assert.Single(targeting.Add(lease.Token, TargetId).Targets);
        f.Db.Services.GetRequiredService<SharedTargetLocks>().RemoveShip(TargetId);
        Assert.Empty(targeting.Read(lease.Token).Targets);
        targeting.Add(lease.Token, TargetId);
        using (var action = StructureAttackTests.Scope(f))
        {
            action.World.Set<WorldNpc>().Find(TargetId)!.X += 1_000_000;
            action.Commit();
        }
        Assert.Empty(targeting.Read(lease.Token).Targets);
        Assert.Throws<InvalidDataException>(() => targeting.Add(lease.Token, TargetId));
        using (var action = StructureAttackTests.Scope(f))
        {
            action.World.Set<WorldNpc>().Find(TargetId)!.X = structure.Position.X + structure.Profile.Radius + 1000;
            action.Commit();
        }
        targeting.Add(lease.Token, TargetId);
        using (var action = StructureAttackTests.Scope(f))
        {
            action.World.Set<SavedStructure>().Find(structure.Id)!.State = StructureState.ArmorReinforced;
            action.Commit();
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
        Assert.Empty(targeting.Read(lease.Token).Targets);
        Assert.Throws<InvalidDataException>(() => targeting.Add(lease.Token, TargetId));
        Assert.Throws<InvalidDataException>(() => targeting.RequireLocked(lease.Token, TargetId));
        Assert.True(scan.ReadyAt < f.Clock.Now);
    }

    [Fact]
    public async Task NativeStructureTargetingKeepsTheScanReplyPendingUntilTheResidentDeadline()
    {
        using var f = StructureAttackTests.Prepare();
        var structure = Prepare(f);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        var other = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(
            other.ShipId,
            structure.Position + new SpacePosition(structure.Profile.Radius + 60000, 0, 0),
            Fixture.SystemId
        );
        other = f.Db.Store.FindPilot(1000002)!;
        using var peerScope = f.Db.Services.CreateScope();
        using PilotCallContext peer = new(
            1000002,
            other.Account,
            [other],
            peerScope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            102,
            SignedSettings.Empty
        );
        peer.Select(other);
        peer.Flight.Snapshot();
        using var scope = f.Db.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
        using PilotCallContext context = new(1000001, pilot.Account, [pilot], factory.Create, 101, SignedSettings.Empty);
        context.Select(pilot);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var adapter = scope.ServiceProvider.GetRequiredService<StructureTargetingAdapter>();
        Success("structureControl", "TakeControl", [structure.Id]);
        var snapshot = Assert.Single(context.Flight.Updates.Select(update => update.Command).OfType<DestinyCommand.SetState>()).Snapshot;
        Assert.Equal(structure.Id, snapshot.Ego);
        Assert.Contains(snapshot.Slims.OfType<NpcSlimItem>(), slim => slim.Npc.Id == TargetId);
        Assert.Contains(snapshot.Slims.OfType<ShipSlimItem>(), slim => slim.Ship.Pilot.ShipId == other.ShipId);
        Assert.DoesNotContain(snapshot.Slims.OfType<ShipSlimItem>(), slim => slim.Ship.Pilot.ShipId == f.ShipId);
        var reply = Assert.IsType<TargetLockReply>(Success("dogmaIM", "AddTarget", [TargetId]).Reply);
        Assert.NotNull(reply.Completion);
        Assert.False(reply.Completion.IsCompleted);
        Assert.Empty(Assert.IsType<BlueList<long>>(Success("dogmaIM", "GetTargets", []).Reply).Items);
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
        adapter.Advance(context);
        Assert.IsType<PilotCallResult.Success>(await reply.Completion);
        Assert.Equal(TargetId, Assert.Single(Assert.IsType<BlueList<long>>(Success("dogmaIM", "GetTargets", []).Reply).Items));
        Assert.Contains(context.Notifications.OfType<TargetChanged>(), change => change.Change == TargetChange.Added);
        PilotSessionTick tick = new(
            f.Clock,
            scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>(),
            scope.ServiceProvider.GetRequiredService<SkillHandler>(),
            f.Db.Services.GetRequiredService<IAsteroidField>(),
            structures: f.Db.Services.GetRequiredService<IStructureContacts>()
        );
        PilotSessionOutput output = new(context, 1000001, 101, _ => Task.CompletedTask, _ => { });
        await tick.AdvanceAsync(context, output, _ => { }, 1000001, 101);
        Assert.True(f.Db.Services.GetRequiredService<SharedTargetLocks>().Contains(structure.Id, TargetId));
        peer.Dispose();
        context.Flight.RefreshPeers();
        Assert.Contains(context.Flight.VisiblePeers, contact => contact.Pilot.ShipId == other.ShipId);
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(20));
        context.Flight.Updates.Clear();
        context.Flight.RefreshPeers();
        Assert.Contains(
            context.Flight.Updates.Select(update => update.Command).OfType<DestinyCommand.RemoveBall>(),
            removed => removed.Id == other.ShipId
        );
        Assert.DoesNotContain(context.Flight.Updates.Select(update => update.Command), command => command is DestinyCommand.SetState);
        Success("dogmaIM", "ClearTargets", []);
        var cancelled = Assert.IsType<TargetLockReply>(Success("dogmaIM", "AddTarget", [TargetId]).Reply);
        Assert.False(cancelled.Completion!.IsCompleted);
        Success("structureControl", "ReleaseControl", []);
        adapter.Advance(context);
        Assert.Equal("TargetingAttemptCancelled", Assert.IsType<PilotCallResult.NamedFailure>(await cancelled.Completion).Name);
        Assert.Contains(context.Notifications.OfType<TargetChanged>(), change => change.Change == TargetChange.Lost);

        PilotCallResult.Success Success(string service, string method, object?[] args)
        {
            var result = router.Call(context, service, method, args, _ => { });
            Assert.True(result is PilotCallResult.Success, $"{service}.{method}: {result}");
            return Assert.IsType<PilotCallResult.Success>(result);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidSignaturesCannotAcquireAndStructureRangeUsesSurfaceDistance(double signature)
    {
        Dictionary<DogmaAttributeId, double> attributes = new()
        {
            [DogmaAttributeId.MaxTargetRange] = 1_000_000,
            [DogmaAttributeId.ScanResolution] = 40,
            [DogmaAttributeId.MaxLockedTargets] = 7,
        };
        var limits = StructureTargetingRules.Limits(attributes);
        Assert.Equal(490_000, limits.Range);
        Assert.True(StructureTargetingRules.InRange(limits, SpacePosition.Zero, 1000, new(491100, 0, 0), 100));
        Assert.False(StructureTargetingRules.InRange(limits, SpacePosition.Zero, 1000, new(491101, 0, 0), 100));
        Assert.Throws<InvalidDataException>(() => StructureTargetingRules.ReadyAt(limits, signature, 1));
    }

    private static StructureSnapshot Prepare(Fixture f)
    {
        var structure = f.Online();
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO TypeAttribute VALUES (601,552,100)");
        }
        using var action = StructureAttackTests.Scope(f);
        action.World.Add(
            new WorldNpc
            {
                Id = TargetId,
                TypeId = 601,
                SystemId = Fixture.SystemId,
                X = structure.Position.X + structure.Profile.Radius + 1000,
                HomeX = structure.Position.X + structure.Profile.Radius + 1000,
                HomeY = 0,
                HomeZ = 0,
                ShieldCharge = 100,
                UpdatedAt = f.Clock.Now,
                MotionAt = f.Clock.Now,
            }
        );
        action.Commit();
        return structure;
    }
}
