using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerTrafficNavigationTests
{
    private const int Source = 30000141;
    private const int Corridor = 30000142;
    private const int Remote = 30000144;
    private const int SourceStation = 60015157;
    private const int DestinationStation = 60003760;

    [Theory]
    [InlineData(0, 1, false, 0, 1, Corridor)]
    [InlineData(1, 1, false, 1, 1, Remote)]
    [InlineData(2, -1, false, 2, -1, Corridor)]
    [InlineData(2, 1, true, 2, -1, Corridor)]
    [InlineData(0, -1, true, 0, -1, null)]
    public void RouteCursorPreservesAdjacentLegsAndTurnsAroundAtStationlessEnd(
        int index,
        int direction,
        bool roundTrip,
        int expectedIndex,
        int expectedDirection,
        int? next
    )
    {
        int[] systems = [Source, Corridor, Remote];
        Assert.Equal(
            new(expectedIndex, expectedDirection, next),
            HaulingRouteRules.Leg(systems, systems[index], index, direction, roundTrip)
        );
        Assert.Throws<InvalidDataException>(() => HaulingRouteRules.Leg(systems, 1, index, direction, roundTrip));
    }

    [Fact]
    public void RepeatedSystemRouteRetainsItsExactSavedOccurrence()
    {
        int[] systems = [Source, Corridor, Remote, Corridor, Source];
        Assert.Equal(new(3, 1, Source), HaulingRouteRules.Leg(systems, Corridor, 3, 1, false));
        Assert.Equal(new(1, -1, Source), HaulingRouteRules.Leg(systems, Corridor, 1, -1, false));
    }

    [Theory]
    [InlineData(HaulerEncounterOpening.Inbound)]
    [InlineData(HaulerEncounterOpening.ApproachingStation)]
    public void FirstEndpointEncounterAlreadyFliesTowardItsLocalStation(HaulerEncounterOpening opening)
    {
        using CapsuleerHaulerTests.Fixture f = new();
        var founder = f.Founder();
        SqliteWorld ships = new(f.Database, f.Clock);
        var pilot = ships.FindPilot(-1, founder.PilotId)!;
        using WorldDbContext world = new(f.Database.Path);
        using CatalogDbContext catalog = new(f.Database.CatalogPath);
        var route = new CapsuleerTrafficRoute(SourceStation, DestinationStation, [Source, Corridor]);
        var work = CapsuleerTrafficFlight.Opening(world, catalog, pilot, route, Source, opening, 0, f.Clock.Now);
        var saved = ships.FindPilot(-1, founder.PilotId)!;
        Assert.Equal(HaulerActivity.Flying, work.Activity);
        Assert.Equal(SourceStation, work.TargetId);
        Assert.Equal(-1, work.RouteDirection);
        Assert.NotNull(saved.Position);
        Assert.True(work.Navigation.Velocity.Length > 0);
        Assert.True(work.Navigation.WarpDestination is not null || work.Navigation.FollowTarget?.Id == SourceStation);
        var navigation = NavigationState.Restore(work.Navigation, saved.Flight);
        var target = SystemObjects.Geometry(catalog, Source).Single(o => o.Id == SourceStation);
        var next = navigation.Advance(saved.ShipId, 1, saved.Flight, saved.Position!, _ => target, []);
        Assert.NotEqual(saved.Position, next.Position);
    }

    [Fact]
    public void StationlessOpeningStartsAtARealGateAndContinuesToNextGate()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        MoveDestinationToRemote(f);
        var founder = f.Founder();
        SqliteWorld ships = new(f.Database, f.Clock);
        var pilot = ships.FindPilot(-1, founder.PilotId)!;
        using WorldDbContext world = new(f.Database.Path);
        using CatalogDbContext catalog = new(f.Database.CatalogPath);
        var route = new CapsuleerTrafficRoute(SourceStation, DestinationStation, [Source, Corridor, Remote]);
        var work = CapsuleerTrafficFlight.Opening(
            world,
            catalog,
            pilot,
            route,
            Corridor,
            HaulerEncounterOpening.ApproachingStation,
            0,
            f.Clock.Now
        );
        var saved = ships.FindPilot(-1, founder.PilotId)!;
        var objects = SystemObjects.Geometry(catalog, Corridor);
        var incoming = objects.Single(o => o.DestinationSystemId == Source);
        var outgoing = objects.Single(o => o.DestinationSystemId == Remote);
        Assert.Equal(Corridor, saved.SystemId);
        Assert.Equal(1, work.RouteIndex);
        Assert.Equal(outgoing.Id, work.TargetId);
        Assert.Equal(GateTravelRules.Arrival(saved.ShipId, incoming.Position, incoming.Radius, saved.Flight.ShipRadius), saved.Position);
        Assert.NotNull(work.Navigation.WarpDestination);
        Assert.True(work.Navigation.Velocity.Length > 0);
    }

    [Fact]
    public void GateFreeLocalInboundStartsAtTheOtherStationsNativeUndock()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using (CatalogDbContext edited = new(f.Database.CatalogPath, writable: true))
        {
            edited.Database.ExecuteSqlRaw(
                """
                UPDATE Station SET SolarSystemId=30000141 WHERE Id=60003760;
                DELETE FROM Stargate;
                DELETE FROM Celestial WHERE GroupId=10;
                """
            );
        }
        var founder = f.Founder();
        SqliteWorld ships = new(f.Database, f.Clock);
        var pilot = ships.FindPilot(-1, founder.PilotId)!;
        using WorldDbContext world = new(f.Database.Path);
        using CatalogDbContext catalog = new(f.Database.CatalogPath);
        var work = CapsuleerTrafficFlight.Opening(
            world,
            catalog,
            pilot,
            new(SourceStation, DestinationStation, [Source]),
            Source,
            HaulerEncounterOpening.Inbound,
            0,
            f.Clock.Now
        );
        var saved = ships.FindPilot(-1, founder.PilotId)!;
        var launch = StationUndocking.Launch(saved.ShipId, saved.Home, saved.Flight);
        Assert.Equal(DestinationStation, saved.StationId);
        Assert.Equal(SourceStation, work.TargetId);
        Assert.Equal(launch.Position, saved.Position);
        Assert.Equal(launch.Velocity, work.Navigation.Velocity);
        Assert.NotNull(work.Navigation.WarpDestination);
        Assert.DoesNotContain(SystemObjects.Geometry(catalog, Source), o => o.DestinationSystemId is not null);
    }

    [Fact]
    public async Task RepeatedAggressionKeepsEndpointRefugeInsteadOfReversingAgain()
    {
        CapsuleerHaulerTests.Fixture f = new();
        var founder = f.Founder();
        var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true).WithPreparedCommunities(f.Database);
        try
        {
            var hauler = host.GetRequiredService<CapsuleerHaulerFactory>().Create(founder.PilotId);
            var job = host.GetRequiredService<ICapsuleerScheduler>().Jobs().Single(j => j.PilotId == founder.PilotId);
            job = hauler.Execute(job, 1);
            hauler.ObserveAggression(founder.FounderShipId);
            job = hauler.Execute(job, 2);
            var refuge = hauler.Read()!.Work;
            Assert.Equal(SourceStation, refuge.DestinationStationId);
            Assert.Equal(-1, refuge.RouteDirection);
            Assert.True(refuge.Retreating);
            if (hauler.Read()!.Pilot.Position is not null)
            {
                hauler.ObserveAggression(founder.FounderShipId);
                job = hauler.Execute(job, 3);
            }
            Assert.Equal(SourceStation, hauler.Read()!.Work.DestinationStationId);
            Assert.Equal(-1, hauler.Read()!.Work.RouteDirection);
            Assert.True(hauler.Read()!.Work.Retreating);
            var threat = Assert.Single(hauler.Read()!.Work.Routine!.Threats);
            Assert.Equal(Source, threat.SystemId);
            // The slow unfitted Badger can need several thousand one-second samples to close on the station.
            for (int turn = 4; turn < 12000 && hauler.Read()!.Pilot.Position is not null; turn++)
            {
                f.Clock.Seconds++;
                job = hauler.Execute(job, turn);
            }
            var docked = hauler.Read()!;
            Assert.True(
                docked.Pilot.Position is null,
                $"Activity={docked.Work.Activity}, target={docked.Work.TargetId}, reason={docked.Work.WaitingReason}, "
                    + $"position={docked.Pilot.Position?.X}/{docked.Pilot.Position?.Y}/{docked.Pilot.Position?.Z}"
            );
            Assert.InRange((docked.Work.DueTicks - docked.Work.ActiveTicks) / TimeSpan.TicksPerSecond, 300, 900);
            Assert.False(docked.Work.Routine!.ShelterPending);
            await host.GetRequiredService<LiveWorld>().FlushAsync();
            using WorldDbContext reopened = new(f.Database.Path);
            var saved = HaulerCheckpoint.Read(reopened.Set<SavedCapsuleerHauler>().Single().Progress)!;
            Assert.Equal(docked.Work.DueTicks, saved.DueTicks);
            Assert.Equal(docked.Work.Routine.Threats, saved.Routine!.Threats);
        }
        finally
        {
            try
            {
                host.Dispose();
                f.Dispose();
            }
            catch (Exception error)
            {
                Console.WriteLine(error);
                throw;
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WitnessedMultiHopAndStationlessTurnaroundKeepTheSameShipAndPersistCursor(bool sameStation)
    {
        using CapsuleerHaulerTests.Fixture f = new();
        MoveDestinationToRemote(f);
        var founder = f.Founder();
        using (WorldDbContext world = new(f.Database.Path))
        {
            var saved = world.Set<SavedCapsuleerHauler>().Single();
            saved.RouteSystems = [Source, Corridor, Remote];
            if (sameStation)
            {
                saved.DestinationStationId = saved.SourceStationId;
            }
            world.SaveChanges();
        }
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true).WithPreparedCommunities(f.Database);
        var hauler = host.GetRequiredService<CapsuleerHaulerFactory>().Create(founder.PilotId);
        host.GetRequiredService<CapsuleerScheduler>().PopulationPrepared(Source);
        var job = host.GetRequiredService<ICapsuleerScheduler>().Jobs().Single(j => j.PilotId == founder.PilotId);
        List<int> visited = [Source];
        bool dockedAtRemote = false;
        for (int i = 0; i < 12000 && visited.Count < 5; i++)
        {
            f.Clock.Seconds++;
            job = hauler.Execute(job, i);
            var observation = hauler.Read()!;
            Assert.Equal(founder.FounderShipId, observation.Pilot.ShipId);
            Assert.Null(observation.Work.WaitingReason);
            if (observation.Work.Activity == HaulerActivity.Jumping)
            {
                Assert.NotNull(observation.Work.DepartureAt);
                Assert.True(observation.Work.DepartureAt <= f.Clock.Now);
            }
            if (observation.Pilot.SystemId == Remote && observation.Work.Activity == HaulerActivity.Docked)
            {
                dockedAtRemote = true;
                Assert.True(job.DueTicks > observation.Work.ActiveTicks);
            }
            if (observation.Pilot.SystemId != visited[^1])
            {
                visited.Add(observation.Pilot.SystemId);
            }
        }
        Assert.Equal<int>([Source, Corridor, Remote, Corridor, Source], visited);
        Assert.True(dockedAtRemote);
        await host.GetRequiredService<LiveWorld>().FlushAsync();
        using WorldDbContext reopened = new(f.Database.Path);
        var progress = HaulerCheckpoint.Read(reopened.Set<SavedCapsuleerHauler>().Single().Progress)!;
        Assert.Equal(2, progress.RouteIndex);
        Assert.Equal(1, progress.RouteDirection);
        Assert.Equal(founder.FounderShipId, progress.ShipId);
    }

    [Fact]
    public void DockDwellSleepsUntilSavedDeadlineAndLegacyDockedWorkRemainsImmediate()
    {
        var work = new HaulerProgress(HaulerActivity.Docked, 10, 30, 1, SourceStation, 0, new NavigationState().Capture());
        work = work with { ActiveTicks = TimeSpan.FromSeconds(10).Ticks, DueTicks = TimeSpan.FromSeconds(30).Ticks };
        Assert.Equal(work.DueTicks, CapsuleerScheduleRules.Next(work, false).DueTicks);
        Assert.Equal(work.ActiveTicks + TimeSpan.TicksPerSecond, CapsuleerScheduleRules.Next(work with { DueTicks = 0 }, false).DueTicks);
    }

    [Fact]
    public void ReappearanceRejectsNonadjacentSystemWithoutChangingShipOrDeadline()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        MoveDestinationToRemote(f);
        var founder = f.Founder();
        using (WorldDbContext world = new(f.Database.Path))
        {
            world.Set<SavedCapsuleerHauler>().Single().RouteSystems = [Source, Corridor, Remote];
            world.SaveChanges();
        }
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true).WithPreparedCommunities(f.Database);
        var hauler = host.GetRequiredService<CapsuleerHaulerFactory>().Create(founder.PilotId);
        var job = host.GetRequiredService<ICapsuleerScheduler>().Jobs().Single(j => j.PilotId == founder.PilotId);
        var before = hauler.Read()!;
        var unchanged = hauler.Reappear(job, TimeSpan.FromHours(1).Ticks, Remote);
        Assert.Equal(job, unchanged);
        Assert.Equal(before.Work, hauler.Read()!.Work);
        Assert.Equal(Source, hauler.SystemId);
        Assert.Equal(before.Pilot.Position, hauler.Read()!.Pilot.Position);
        Assert.Equal(founder.FounderShipId, hauler.Read()!.Pilot.ShipId);
    }

    [Fact]
    public void RememberedReappearanceDoesNotAdvertiseANewGateActivation()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        var founder = f.Founder();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true).WithPreparedCommunities(f.Database);
        var hauler = host.GetRequiredService<CapsuleerHaulerFactory>().Create(founder.PilotId);
        var job = host.GetRequiredService<ICapsuleerScheduler>().Jobs().Single(j => j.PilotId == founder.PilotId);
        hauler.Reappear(job, TimeSpan.FromHours(1).Ticks, Corridor);
        var appearance = hauler.Read()!;
        Assert.Equal(Corridor, appearance.Pilot.SystemId);
        Assert.Null(appearance.Work.ArrivalGateId);
        Assert.Null(appearance.Work.ArrivalAt);
        Assert.Null(appearance.Work.DepartureAt);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EightJumpJourneyRetainsShipDestinationAndCursorAcrossRestart(bool background, bool scheduled)
    {
        using CapsuleerHaulerTests.Fixture f = new();
        var founder = f.Founder();
        const int firstSystem = 30001001;
        const int jumps = 8;
        int[] route = [Source, .. Enumerable.Range(firstSystem, jumps)];
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("DELETE FROM Stargate");
            for (int index = 1; index < route.Length; index++)
            {
                int system = route[index];
                int previous = route[index - 1];
                int departure = 50010000 + index * 2;
                int arrival = departure + 1;
                catalog.Database.ExecuteSql($"INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES ({system},20000020,'Journey')");
                catalog.Database.ExecuteSql(
                    $"""
                    INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z) VALUES
                        ({departure},{previous},16,10,3532,900000000000,0,0),
                        ({arrival},{system},16,10,3532,-900000000000,0,0);
                    INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES
                        ({departure},{previous},{arrival}),({arrival},{system},{departure});
                    """
                );
            }
            catalog.Database.ExecuteSql($"UPDATE Station SET SolarSystemId={route[^1]} WHERE Id={DestinationStation}");
        }
        using (WorldDbContext world = new(f.Database.Path))
        {
            var saved = world.Set<SavedCapsuleerHauler>().Single();
            saved.RouteSystems = route;
            var work = new HaulerProgress(
                HaulerActivity.Docked,
                0,
                0,
                founder.FounderShipId,
                DestinationStation,
                0,
                new NavigationState().Capture(),
                Journey: new(TrafficJourneyKind.LongDistance, SourceStation, SourceStation, DestinationStation, route, 0)
            );
            world.SaveChanges();
            new HaulerCheckpoint(founder.PilotId, work).Apply(world);
        }
        var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true).WithPreparedCommunities(f.Database);
        IHumanPresenceLease? human = null;
        try
        {
            var hauler = host.GetRequiredService<CapsuleerHaulerFactory>().Create(founder.PilotId);
            var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
            if (scheduled)
            {
                human = host.GetRequiredService<IHumanPresence>().Select(90000001, Source);
            }
            var job = scheduler.Jobs().Single(j => j.PilotId == founder.PilotId);
            HaulerObservation Current() => scheduled ? scheduler.Hauler(founder.PilotId)! : hauler.Read()!;
            List<int> visited = [Source];
            bool restarted = false;
            for (int turn = 0; turn < 12000 && Current().Pilot.StationId != DestinationStation; turn++)
            {
                f.Clock.Seconds++;
                if (scheduled)
                {
                    scheduler.Run(turn);
                }
                else
                {
                    job = hauler.Execute(job, turn, background);
                }
                var observation = Current();
                Assert.Null(observation.Work.WaitingReason);
                Assert.Equal(founder.FounderShipId, observation.Pilot.ShipId);
                if (observation.Pilot.SystemId != visited[^1])
                {
                    visited.Add(observation.Pilot.SystemId);
                    human?.Transfer(observation.Pilot.SystemId);
                }
                if (!restarted && observation.Work.RouteIndex >= jumps / 2)
                {
                    human?.Dispose();
                    await host.GetRequiredService<LiveWorld>().FlushAsync();
                    host.Dispose();
                    host = ServerServices.Build(f.Database, f.Clock, liveWorld: true).WithPreparedCommunities(f.Database);
                    hauler = host.GetRequiredService<CapsuleerHaulerFactory>().Create(founder.PilotId);
                    scheduler = host.GetRequiredService<ICapsuleerScheduler>();
                    if (scheduled)
                    {
                        human = host.GetRequiredService<IHumanPresence>().Select(90000001, observation.Pilot.SystemId);
                    }
                    job = host.GetRequiredService<ICapsuleerScheduler>().Jobs().Single(j => j.PilotId == founder.PilotId);
                    restarted = true;
                }
            }
            Assert.True(restarted);
            if (scheduled)
            {
                Assert.Equal(Source, visited[0]);
                Assert.Equal(route[^1], visited[^1]);
                Assert.All(
                    visited.Zip(visited.Skip(1)),
                    leg => Assert.True(Array.IndexOf(route, leg.First) < Array.IndexOf(route, leg.Second))
                );
            }
            else
            {
                Assert.Equal(route, visited);
            }
            Assert.Equal(DestinationStation, Current().Pilot.StationId);
            Assert.Null(Current().Pilot.Position);
            Assert.Equal(jumps, Current().Work.RouteIndex);
        }
        finally
        {
            human?.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void VisitorVisitsAnotherStationBeforeReturningToItsOrigin()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        const int thirdStation = 60009999;
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql(
                $"""
                INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                VALUES ({thirdStation},52678,1000035,{Corridor},14,40009087,900000000000,0,0)
                """
            );
        }
        long pilot = new CapsuleerProvisioner(f.Database, f.Clock)
            .EnsureInbound(new(Source, Corridor, 50009991, 50009992), sourceObserved: true)!
            .Value;
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true).WithPreparedCommunities(f.Database);
        var hauler = host.GetRequiredService<CapsuleerHaulerFactory>().Create(pilot);
        var job = host.GetRequiredService<ICapsuleerScheduler>().Jobs().Single(row => row.PilotId == pilot);
        var original = hauler.Read()!;
        int home = original.Work.Journey!.HomeStationId;
        bool visitedThirdStation = false;
        for (int turn = 0; turn < 4000; turn++)
        {
            f.Clock.Seconds = checked((int)(job.DueTicks / TimeSpan.TicksPerSecond));
            job = hauler.Execute(job, turn);
            var observation = hauler.Read()!;
            Assert.Null(observation.Work.WaitingReason);
            Assert.Equal(original.Pilot.ShipId, observation.Pilot.ShipId);
            if (observation.Work.Journey!.Sequence == 1)
            {
                Assert.Equal(thirdStation, observation.Work.Journey.DestinationStationId);
            }
            if (observation.Pilot.Position is null && observation.Pilot.StationId == thirdStation)
            {
                visitedThirdStation = true;
            }
            if (observation.Work.Journey.Sequence == 2)
            {
                Assert.True(visitedThirdStation);
                Assert.Equal(home, observation.Work.Journey.DestinationStationId);
                return;
            }
        }
        Assert.Fail("The visitor did not complete its onward stop and homeward departure.");
    }

    private static void MoveDestinationToRemote(CapsuleerHaulerTests.Fixture fixture)
    {
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            UPDATE Station SET SolarSystemId=30000144 WHERE Id=60003760;
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/solarsystemcontent','30000144','{{"security":1.0}}');
            """
        );
    }
}
