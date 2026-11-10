using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Market;
using Eden.World.Runtime;
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StationlessLaneContinuesToItsSavedRemoteStationAndPersistsCursor(bool reverseGate, bool imported)
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false);
        const int remote = 30000144;
        const long remoteGate = 50001249;
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "UPDATE Station SET SolarSystemId=30000144; UPDATE Station SET SolarSystemId=30000141 WHERE Id=60015157"
            );
        }
        if (imported)
        {
            TrafficSnapshotTests.Install(f.Database, TrafficSnapshotTests.Profile());
        }
        var gate = reverseGate ? new(Source, Observer, s_gate.DepartureGateId, s_gate.ArrivalGateId) : s_gate;
        long pilot = CreateLegacyInbound(f, gate);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var hauler = host.GetRequiredService<CapsuleerHaulerFactory>().Create(pilot);
        var job = host.GetRequiredService<ICapsuleerScheduler>().Jobs().Single(j => j.PilotId == pilot);
        long ship = hauler.Read()!.Pilot.ShipId;
        job = hauler.ArmInbound(job, 0, gate);
        job = hauler.Execute(job, 0, background: true);
        List<int> visited = [hauler.SystemId];
        for (int seconds = 1; seconds < 2000 && hauler.Read()!.Pilot.Position is not null; seconds++)
        {
            var observation = hauler.Read()!;
            if (observation.Pilot.SystemId == Source)
            {
                Assert.Equal(reverseGate ? remoteGate : s_gate.DepartureGateId, observation.Work.TargetId);
                Assert.Equal(1, observation.Work.RouteIndex);
                Assert.Equal(1, observation.Work.RouteDirection);
            }
            f.Clock.Seconds++;
            job = hauler.Execute(job, seconds);
            Assert.Null(hauler.Read()!.Work.WaitingReason);
            Assert.Equal(ship, hauler.Read()!.Pilot.ShipId);
            if (hauler.SystemId != visited[^1])
            {
                visited.Add(hauler.SystemId);
            }
        }
        Assert.Equal<int>(reverseGate ? [Source, remote] : [Observer], visited);
        Assert.Null(hauler.Read()!.Pilot.Position);
        Assert.Equal(HaulerActivity.Docked, hauler.Read()!.Work.Activity);
        await host.GetRequiredService<LiveWorld>().FlushAsync();
        using WorldDbContext reopened = new(f.Database.Path);
        var saved = reopened.Set<SavedCapsuleerHauler>().Single(h => h.PilotId == pilot);
        var progress = HaulerCheckpoint.Read(saved.Progress)!;
        Assert.Equal(2, progress.RouteIndex);
        Assert.Equal(1, progress.RouteDirection);
        Assert.Equal(ship, progress.ShipId);
        Assert.Equal(saved.DestinationStationId, hauler.Read()!.Pilot.StationId);
    }

    [Theory]
    [InlineData("arm")]
    [InlineData("pause")]
    [InlineData("release")]
    public void FailedInboundTransitionPreservesResidentAndSavedProgress(string transition)
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        long pilot = CreateLegacyInbound(f, s_gate);
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
        long pilot = CreateLegacyInbound(f, s_gate);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: false);
        var hauler = host.GetRequiredService<CapsuleerHaulerFactory>().Create(pilot);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        // Restore begins paused until an actual gate observer enrolls this lane.
        var job = scheduler.Jobs().Single(j => j.PilotId == pilot);
        _ = hauler.Read();
        job = hauler.ArmInbound(job, 0, s_gate);
        long originalDue = job.DueTicks;
        long originalSequence = hauler.Read()!.Work.DwellSequence;
        long watched = TimeSpan.FromSeconds(10).Ticks;
        job = hauler.PauseInbound(job, watched);
        Assert.Equal(originalDue, job.DueTicks);
        Assert.True(hauler.Read()!.Work.InboundPaused);
        long returned = TimeSpan.FromMinutes(10).Ticks;
        job = hauler.ArmInbound(job, returned, s_gate);
        Assert.Equal(originalDue - watched, job.DueTicks - returned);
        Assert.Equal(originalSequence, hauler.Read()!.Work.DwellSequence);
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
    public void SavedWaitSequenceVariesTripsWithinBoundsAndRoundTripsThroughLegacyAndCurrentProgress()
    {
        const long pilot = 90000123;
        var gateDelays = Enumerable.Range(0, 32).Select(sequence => HaulingRules.InboundDwellSeconds(4, 0, pilot, sequence)).ToArray();
        var stationDelays = Enumerable.Range(0, 32).Select(sequence => HaulingRules.StationDwellSeconds(pilot, sequence)).ToArray();
        Assert.All(gateDelays, delay => Assert.InRange(delay, 35, 75));
        Assert.All(stationDelays, delay => Assert.InRange(delay, 15, 45));
        Assert.True(gateDelays.Distinct().Count() > 1);
        Assert.True(stationDelays.Distinct().Count() > 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => HaulingRules.StationDwellSeconds(pilot, -1));
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        long actor = CreateLegacyInbound(f, s_gate);
        using WorldDbContext world = new(f.Database.Path);
        var saved = world.Set<SavedCapsuleerHauler>().Single(h => h.PilotId == actor);
        var work = HaulerCheckpoint.Read(saved.Progress)!;
        Assert.Equal(1, work.DwellSequence);
        var later = work with
        {
            DwellSequence = 17,
            Journey = new(
                TrafficJourneyKind.Regional,
                saved.SourceStationId,
                saved.SourceStationId,
                saved.DestinationStationId,
                saved.RouteSystems!,
                1
            ),
        };
        new HaulerCheckpoint(actor, later).Apply(world);
        using WorldDbContext reopened = new(f.Database.Path);
        var restored = HaulerCheckpoint.Read(reopened.Set<SavedCapsuleerHauler>().Single(h => h.PilotId == actor).Progress)!;
        Assert.Equal(later with { Journey = null }, restored with { Journey = null });
        Assert.Equal(later.Journey!.Systems, restored.Journey!.Systems);
        Assert.Equal(later.Journey, restored.Journey with { Systems = later.Journey.Systems });
        Assert.Equal(
            HaulingRules.InboundDwellSeconds(4, 0, actor, later.DwellSequence),
            HaulingRules.InboundDwellSeconds(4, 0, actor, restored.DwellSequence)
        );
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(saved.Progress!)!.AsObject();
        Assert.True(legacy.Remove(nameof(HaulerProgress.DwellSequence)));
        Assert.Equal(0, HaulerCheckpoint.Read(legacy.ToJsonString())!.DwellSequence);
    }

    [Fact]
    public void WarpDisruptionBlocksHiddenInboundTransferUntilItClears()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        long pilot = CreateLegacyInbound(f, s_gate);
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
            host.GetRequiredService<MarketChangeSignal>(),
            host.GetRequiredService<CapsuleerDefense>(),
            host.GetRequiredService<CapsuleerResupply>(),
            host.GetRequiredService<CapsuleerRatting>()
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DestroyedInboundShipCannotCrossOrRegainItsLostHull(bool imported)
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        if (imported)
        {
            TrafficSnapshotTests.Install(f.Database, TrafficSnapshotTests.Profile());
        }
        long pilotId = CreateLegacyInbound(f, s_gate);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyPausedArrivalUpgradePreservesRemainingDelayAndShip(bool liveWorld)
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        long pilot = CreateLegacyInbound(f, s_gate);
        long ship;
        using (WorldDbContext world = new(f.Database.Path))
        {
            var row = world.Set<SavedCapsuleerHauler>().Single();
            ship = row.FounderShipId;
            var work = HaulerCheckpoint.Read(row.Progress)!;
            new HaulerCheckpoint(
                pilot,
                work with
                {
                    ActiveTicks = TimeSpan.FromSeconds(10).Ticks,
                    DueTicks = TimeSpan.FromSeconds(90).Ticks,
                    InboundPaused = true,
                    Journey = null,
                }
            ).Apply(world);
            world.Set<SavedCapsuleerClock>().Single().ElapsedTicks = TimeSpan.FromSeconds(600).Ticks;
            world.Set<SavedCapsuleerJob>().Single().DueTicks = TimeSpan.FromSeconds(90).Ticks;
            world.SaveChanges();
        }
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: liveWorld);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        var job = scheduler.Jobs().Single();
        Assert.Equal(TimeSpan.FromSeconds(680).Ticks, job.DueTicks);
        var restored = scheduler.Hauler(pilot)!;
        Assert.False(restored.Work.InboundPaused);
        Assert.Equal(TimeSpan.FromSeconds(600).Ticks, restored.Work.ActiveTicks);
        Assert.Equal(ship, restored.Pilot.ShipId);
        Assert.Equal(job, scheduler.Jobs().Single());
    }

    private static long CreateLegacyInbound(CapsuleerHaulerTests.Fixture fixture, WatchedTrafficGate gate)
    {
        long pilot = new CapsuleerProvisioner(fixture.Database, fixture.Clock).EnsureInbound(gate)!.Value;
        using WorldDbContext world = new(fixture.Database.Path);
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        var row = world.Set<SavedCapsuleerHauler>().Single(value => value.PilotId == pilot);
        var route = CapsuleerTrafficFlight.Route(row, catalog);
        var leg = HaulingRouteRules.Crossing(route.Systems, gate.SourceSystemId, gate.ObserverSystemId);
        var work = HaulerCheckpoint.Read(row.Progress)!;
        var departure = SystemObjects.Geometry(catalog, gate.SourceSystemId).Single(body => body.Id == gate.DepartureGateId);
        var ship = world.Ships.Single(value => value.Id == row.FounderShipId);
        double radius = catalog.RequireItem(ship.TypeId).Radius ?? 0;
        var position = departure.Position + new SpacePosition(departure.Radius + radius + CapsuleerTrafficFlight.ApproachRange, 0, 0);
        SqliteWorld.SetPosition(world, catalog, ship.Id, position, gate.SourceSystemId, fixture.Clock.Now);
        int dwell = HaulingRules.InboundDwellSeconds(
            catalog.Definitions.GatesInSystem(gate.ObserverSystemId).Length,
            catalog.Definitions.StationsInSystem(gate.ObserverSystemId).Length,
            pilot
        );
        // Exercise compatibility with the previously saved gate-side waiting state.
        work = work with
        {
            Activity = HaulerActivity.Inbound,
            DueStamp = dwell,
            DueTicks = TimeSpan.FromSeconds(dwell).Ticks,
            TargetId = gate.DepartureGateId,
            RouteIndex = leg.Index,
            RouteDirection = leg.Direction,
            DwellSequence = 1,
            Journey = null,
        };
        new HaulerCheckpoint(pilot, work).Apply(world);
        var job = world.Set<SavedCapsuleerJob>().Single(value => value.PilotId == pilot);
        job.Action = CapsuleerAction.GateTransfer;
        job.DueTicks = work.DueTicks;
        world.SaveChanges();
        return pilot;
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
