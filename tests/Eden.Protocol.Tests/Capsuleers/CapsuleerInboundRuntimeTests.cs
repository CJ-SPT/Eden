using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Market;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerInboundRuntimeTests
{
    private const int Observer = 30000141;
    private const int Source = 30000142;
    private static readonly WatchedTrafficGate s_gate = new(Observer, Source, 50009991, 50009992);

    [Theory]
    [InlineData("arm")]
    [InlineData("pause")]
    [InlineData("release")]
    public void FailedInboundTransitionPreservesResidentAndSavedProgress(string transition)
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        long pilot = new CapsuleerProvisioner(f.Database, f.Clock).EnsureInbound(s_gate)!.Value;
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: false);
        var hauler = host.GetRequiredService<CapsuleerHaulerFactory>().Create(pilot);
        CapsuleerJob job;
        using (WorldDbContext world = new(f.Database.Path))
        {
            var saved = world.Set<SavedCapsuleerJob>().Single();
            job = new(saved.PilotId, saved.Revision, saved.Sequence, saved.DueTicks, saved.Action);
        }
        _ = hauler.Read();
        if (transition == "arm")
        {
            job = hauler.ReleaseInbound(job, 0);
        }
        var before = hauler.Read()!;
        string? progress;
        using (WorldDbContext world = new(f.Database.Path))
        {
            progress = world.Set<SavedCapsuleerHauler>().Single().Progress;
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER reject_inbound_progress BEFORE UPDATE OF Progress ON WorldCapsuleerHauler
                BEGIN SELECT RAISE(ABORT,'forced inbound checkpoint failure'); END;
                """
            );
        }
        CapsuleerJob Change() =>
            transition switch
            {
                "arm" => hauler.ArmInbound(job, TimeSpan.FromSeconds(10).Ticks, s_gate),
                "pause" => hauler.PauseInbound(job, TimeSpan.FromSeconds(10).Ticks),
                _ => hauler.ReleaseInbound(job, TimeSpan.FromSeconds(10).Ticks),
            };
        Assert.Throws<SqliteException>(() => Change());
        Assert.Equal(before.Work, hauler.Read()!.Work);
        Assert.Equal(before.Pilot.Position, hauler.Read()!.Pilot.Position);
        using (WorldDbContext world = new(f.Database.Path))
        {
            Assert.Equal(progress, world.Set<SavedCapsuleerHauler>().Single().Progress);
            var saved = world.Set<SavedCapsuleerJob>().Single();
            Assert.Equal(job, new(saved.PilotId, saved.Revision, saved.Sequence, saved.DueTicks, saved.Action));
            world.Database.ExecuteSqlRaw("DROP TRIGGER reject_inbound_progress");
        }
        Assert.True(Change().Revision > job.Revision);
        Assert.NotEqual(before.Work, hauler.Read()!.Work);
    }

    [Fact]
    public void PausedInboundRetainsRemainingObservedDelayAndOneDurableTransfer()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        long pilot = new CapsuleerProvisioner(f.Database, f.Clock).EnsureInbound(s_gate)!.Value;
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: false);
        var hauler = host.GetRequiredService<CapsuleerHaulerFactory>().Create(pilot);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        // Restore begins paused until an actual gate observer enrolls this lane.
        var job = scheduler.Jobs().Single(j => j.PilotId == pilot);
        _ = hauler.Read();
        job = hauler.ArmInbound(job, 0, s_gate);
        long originalDue = job.DueTicks;
        long watched = TimeSpan.FromSeconds(10).Ticks;
        job = hauler.PauseInbound(job, watched);
        Assert.Equal(originalDue, job.DueTicks);
        Assert.True(hauler.Read()!.Work.InboundPaused);
        long returned = TimeSpan.FromMinutes(10).Ticks;
        job = hauler.ArmInbound(job, returned, s_gate);
        Assert.Equal(originalDue - watched, job.DueTicks - returned);
        Assert.False(hauler.Read()!.Work.InboundPaused);
        Assert.Equal(Source, hauler.SystemId);
        f.Clock.Seconds += 700;
        var next = hauler.Execute(job, 700, background: true);
        var arrival = hauler.Read()!;
        Assert.Equal(Observer, arrival.Pilot.SystemId);
        Assert.Equal(HaulerActivity.Flying, arrival.Work.Activity);
        Assert.True(arrival.Work.ArrivalUntil > arrival.Work.Stamp);
        Assert.Equal(s_gate.ArrivalGateId, arrival.Work.ArrivalGateId);
        Assert.Equal(f.Clock.Now, arrival.Work.ArrivalAt);
        Assert.Equal(next, hauler.ArmInbound(next, next.DueTicks, s_gate));
        Assert.Equal(arrival.Pilot.ShipId, hauler.Read()!.Pilot.ShipId);
    }

    [Theory]
    [InlineData(4, 0, 35, 75)]
    [InlineData(2, 0, 65, 105)]
    [InlineData(1, 0, 95, 135)]
    public void BusyAndQuietRoutesHaveBoundedDifferentCadences(int gates, int stations, int minimum, int maximum)
    {
        var delays = Enumerable.Range(1, 100).Select(id => HaulingRules.InboundDwellSeconds(gates, stations, id)).ToArray();
        Assert.All(delays, delay => Assert.InRange(delay, minimum, maximum));
        Assert.True(delays.Distinct().Count() > 1);
    }

    [Fact]
    public void WarpDisruptionBlocksHiddenInboundTransferUntilItClears()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        long pilot = new CapsuleerProvisioner(f.Database, f.Clock).EnsureInbound(s_gate)!.Value;
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: false);
        BlockingTackle tackle = new(host.GetRequiredService<IPilotTackle>());
        CapsuleerHauler hauler = new(
            pilot,
            f.Database,
            host.GetRequiredService<SqliteWorld>(),
            host.GetRequiredService<CapsuleerGeometry>(),
            tackle,
            host.GetRequiredService<INpcCombat>(),
            host.GetRequiredService<INpcSimulation>(),
            host.GetRequiredService<INpcMissiles>(),
            host.GetRequiredService<INpcElectronicWarfare>(),
            host.GetRequiredService<IPilotHealth>(),
            f.Clock,
            host.GetRequiredService<MarketChangeSignal>()
        );
        var job = host.GetRequiredService<ICapsuleerScheduler>().Jobs().Single(j => j.PilotId == pilot);
        _ = hauler.Read();
        job = hauler.ArmInbound(job, 0, s_gate);
        var before = hauler.Read()!;
        var retry = hauler.Execute(job, 1, background: true);
        Assert.Equal(Source, hauler.SystemId);
        Assert.Equal(before.Pilot.Position, hauler.Read()!.Pilot.Position);
        Assert.Equal(HaulerActivity.Waiting, hauler.Read()!.Work.Activity);
        Assert.Contains("Warp disruption", hauler.Read()!.Work.WaitingReason);
        tackle.Blocked = false;
        var armed = hauler.ArmInbound(retry, retry.DueTicks, s_gate);
        hauler.Execute(armed, 2, background: true);
        Assert.Equal(Observer, hauler.SystemId);
        Assert.Equal(before.Pilot.ShipId, hauler.Read()!.Pilot.ShipId);
    }

    [Fact]
    public void DestroyedInboundShipCannotCrossOrRegainItsLostHull()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        long pilotId = new CapsuleerProvisioner(f.Database, f.Clock).EnsureInbound(s_gate)!.Value;
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var hauler = host.GetRequiredService<CapsuleerHaulerFactory>().Create(pilotId);
        var job = host.GetRequiredService<ICapsuleerScheduler>().Jobs().Single(j => j.PilotId == pilotId);
        var before = hauler.Read()!;
        job = hauler.ArmInbound(job, 0, s_gate);
        using (WorldDbContext world = new(f.Database.Path))
        using (CatalogDbContext catalog = new(f.Database.CatalogPath))
        {
            using var transaction = world.Database.BeginTransaction();
            var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
            const long attacker = 700000003;
            world.Add(
                new WorldNpc
                {
                    Id = attacker,
                    TypeId = 601,
                    CorporationId = 1000035,
                    SystemId = Source,
                }
            );
            world.SaveChanges();
            var defense = ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, pilotId));
            PilotHealthStore.Apply(world, before.Pilot.ShipId, defense, new DamageVector(1000000, 0, 0, 0), f.Clock.Now);
            ShipLossPersistence.Apply(
                world,
                catalog,
                pilot,
                attacker,
                before.Pilot.ShipId,
                Source,
                pilot.ActiveShip.Position!,
                f.Clock.Now,
                host.GetRequiredService<IShipLootRoll>()
            );
            world.SaveChanges();
            transaction.Commit();
        }
        var next = hauler.Execute(job, 1, background: true);
        Assert.Equal(Source, hauler.SystemId);
        Assert.Equal(FleetDefinitions.CapsuleGroup, hauler.Read()!.Pilot.ShipGroupId);
        Assert.NotEqual(before.Pilot.ShipId, hauler.Read()!.Pilot.ShipId);
        Assert.Equal(next, hauler.ArmInbound(next, next.DueTicks, s_gate));
        Assert.False(hauler.IsInbound);
        using WorldDbContext reopened = new(f.Database.Path);
        Assert.DoesNotContain(reopened.Ships, ship => ship.OwnerId == pilotId && ship.TypeId == HaulingRules.BadgerType);
    }

    private sealed class BlockingTackle(IPilotTackle actual) : IPilotTackle
    {
        internal bool Blocked { get; set; } = true;

        public IReadOnlyList<TackleEffect> Incoming(long shipId) =>
            Blocked
                ?
                [
                    new(
                        1,
                        2,
                        new(3, 4, DogmaEffect.WarpDisruptor, 10000, 0, 250000, 1, 0, 0, 0),
                        new(shipId, TackleTargetKind.Ship),
                        0,
                        long.MaxValue
                    ),
                ]
                : actual.Incoming(shipId);

        public IReadOnlyDictionary<DogmaAttributeId, double> ShipAttributes(long pilotId)
        {
            var attributes = actual.ShipAttributes(pilotId).ToDictionary();
            attributes[DogmaAttributeId.WarpScrambleStatus] = Blocked ? 1 : 0;
            return attributes;
        }

        public TackleActivation Activate(
            long pilotId,
            long moduleId,
            DogmaEffect effect,
            TackleTarget? target,
            IReadOnlySet<long> lockedTargets,
            long now
        ) => actual.Activate(pilotId, moduleId, effect, target, lockedTargets, now);

        public void Sweep(long now) => actual.Sweep(now);

        public void Stop(long pilotId, long shipId, long moduleId) => actual.Stop(pilotId, shipId, moduleId);

        public void Cancel(long pilotId, long shipId) => actual.Cancel(pilotId, shipId);

        public void Disconnect(long pilotId, long shipId) => actual.Disconnect(pilotId, shipId);
    }
}
