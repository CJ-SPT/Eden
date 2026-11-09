using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerTrafficRoutesTests
{
    private const int FirstSystem = 30001001;
    private const int FirstStation = 60001001;

    [Fact]
    public void StationlessTransitUsesReciprocalMultiJumpPathsAcrossAllSecurity()
    {
        using Fixture fixture = new();
        fixture.AddStation(FirstStation, FirstSystem);
        fixture.AddStation(FirstStation + 1, FirstSystem + 5);
        for (int offset = 0; offset < 5; offset++)
        {
            fixture.Connect(FirstSystem + offset, FirstSystem + offset + 1);
        }
        using CatalogDbContext catalog = new(fixture.Path);
        var plan = CapsuleerTrafficRoutes.Read(catalog, FirstSystem + 2);
        Assert.Equal(2, plan.GateCount);
        Assert.Equal(0, plan.StationCount);
        Assert.Equal(2, plan.Routes.Length);
        var forward = Assert.Single(plan.Routes, route => route.SourceStationId == FirstStation);
        Assert.Equal(FirstStation + 1, forward.DestinationStationId);
        Assert.Equal(Enumerable.Range(FirstSystem, 6), forward.Systems);
        AssertPathsAreReal(catalog, plan, FirstSystem + 2);
    }

    [Fact]
    public void OneReachableStationProducesRealOutAndBackTurnarounds()
    {
        using Fixture fixture = new();
        fixture.AddStation(FirstStation, FirstSystem);
        fixture.Connect(FirstSystem, FirstSystem + 1);
        fixture.Connect(FirstSystem + 1, FirstSystem + 2);
        using CatalogDbContext catalog = new(fixture.Path);
        var transit = CapsuleerTrafficRoutes.Read(catalog, FirstSystem + 2);
        Assert.Empty(transit.Routes);
        Assert.Empty(CapsuleerTrafficRoutes.Read(catalog, FirstSystem).Routes);
    }

    [Fact]
    public void LocalTripsUseDistinctStationsAndIsolatedSingleStationsHaveNoImaginaryTrips()
    {
        using Fixture fixture = new();
        fixture.AddStation(FirstStation, FirstSystem);
        using (CatalogDbContext catalog = new(fixture.Path))
        {
            Assert.Empty(CapsuleerTrafficRoutes.Read(catalog, FirstSystem).Routes);
            Assert.Empty(CapsuleerTrafficRoutes.Read(catalog, FirstSystem + 1).Routes);
        }
        fixture.AddStation(FirstStation + 1, FirstSystem);
        using CatalogDbContext updated = new(fixture.Path);
        var plan = CapsuleerTrafficRoutes.Read(updated, FirstSystem);
        Assert.Equal(0, plan.GateCount);
        Assert.Equal(2, plan.StationCount);
        Assert.Equal(2, plan.Routes.Length);
        Assert.All(
            plan.Routes,
            route =>
            {
                Assert.NotEqual(route.SourceStationId, route.DestinationStationId);
                Assert.Equal([FirstSystem], route.Systems);
            }
        );
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NonreciprocalOrMissingStaticGatesCannotCreateRoutes(bool nonreciprocal)
    {
        using Fixture fixture = new();
        fixture.AddStation(FirstStation, FirstSystem);
        fixture.AddStation(FirstStation + 1, FirstSystem + 1);
        fixture.Connect(FirstSystem, FirstSystem + 1);
        using (CatalogDbContext edit = new(fixture.Path, writable: true))
        {
            if (nonreciprocal)
            {
                edit.Database.ExecuteSqlRaw("UPDATE Stargate SET DestinationId=Id WHERE SolarSystemId=30001002");
            }
            else
            {
                edit.Database.ExecuteSqlRaw("DELETE FROM Celestial WHERE SolarSystemId=30001002");
            }
        }
        using CatalogDbContext catalog = new(fixture.Path);
        var plan = CapsuleerTrafficRoutes.Read(catalog, FirstSystem);
        Assert.Equal(0, plan.GateCount);
        Assert.Empty(plan.Routes);
    }

    [Fact]
    public void StationlessDeadEndRetracesRealLinksBetweenDistinctStations()
    {
        using Fixture fixture = new();
        fixture.AddStation(FirstStation, FirstSystem);
        fixture.AddStation(FirstStation + 1, FirstSystem);
        fixture.Connect(FirstSystem, FirstSystem + 1);
        fixture.Connect(FirstSystem + 1, FirstSystem + 2);
        using CatalogDbContext catalog = new(fixture.Path);
        var plan = CapsuleerTrafficRoutes.Read(catalog, FirstSystem + 2);
        Assert.Empty(plan.Routes);
    }

    [Fact]
    public void BranchEndpointsProvideBoundedVarietyAndWarmedPlansShareCatalogMemoization()
    {
        using Fixture fixture = new();
        for (int offset = 1; offset <= 10; offset++)
        {
            fixture.AddStation(FirstStation + offset, FirstSystem + offset);
            fixture.AddStation(FirstStation + 100 + offset, FirstSystem + offset);
            fixture.Connect(FirstSystem, FirstSystem + offset);
        }
        using CatalogDbContext catalog = new(fixture.Path);
        var original = CapsuleerTrafficRoutes.Read(catalog, FirstSystem);
        Assert.Equal(8, original.Routes.Length);
        Assert.True(original.Routes.Select(route => route.SourceStationId).Distinct().Count() >= 4);
        Assert.Equal(8, original.Routes.Select(route => (route.SourceStationId, route.DestinationStationId)).Distinct().Count());
        AssertPathsAreReal(catalog, original, FirstSystem);
        using (var metrics = SessionPerformance.Begin("warm traffic route plans", _ => { }, force: true)!)
        using (CatalogDbContext second = new(fixture.Path))
        {
            Assert.Same(original, CapsuleerTrafficRoutes.Read(second, FirstSystem));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        fixture.AddStation(FirstStation, FirstSystem);
        using CatalogDbContext updated = new(fixture.Path);
        var current = CapsuleerTrafficRoutes.Read(updated, FirstSystem);
        Assert.NotSame(original, current);
        Assert.Equal(0, original.StationCount);
        Assert.Equal(1, current.StationCount);
    }

    [Fact]
    public void LocalTripsRemainAvailableAmongManyRemoteBranches()
    {
        using Fixture fixture = new();
        fixture.AddStation(FirstStation, FirstSystem);
        fixture.AddStation(FirstStation + 100, FirstSystem);
        for (int offset = 1; offset <= 10; offset++)
        {
            fixture.AddStation(FirstStation + offset, FirstSystem + offset);
            fixture.Connect(FirstSystem, FirstSystem + offset);
        }
        using CatalogDbContext catalog = new(fixture.Path);
        var plan = CapsuleerTrafficRoutes.Read(catalog, FirstSystem);
        Assert.Equal(8, plan.Routes.Length);
        Assert.Equal(2, plan.Routes.Count(route => route.Systems.Length == 1));
        Assert.Contains(plan.Routes, route => route.Systems.Length > 1);
        AssertPathsAreReal(catalog, plan, FirstSystem);
    }

    [Theory]
    [InlineData(TrafficJourneyKind.Local, 0, 2)]
    [InlineData(TrafficJourneyKind.Regional, 3, 7)]
    [InlineData(TrafficJourneyKind.LongDistance, 8, 10)]
    public void DestinationJourneysHaveRealEndpointsDistanceBandsAndNoRepeatedSystems(TrafficJourneyKind kind, int minimum, int maximum)
    {
        using Fixture fixture = new();
        for (int offset = 0; offset <= 10; offset++)
        {
            fixture.AddStation(FirstStation + offset, FirstSystem + offset);
            if (offset > 0)
            {
                fixture.Connect(FirstSystem + offset - 1, FirstSystem + offset);
            }
        }
        using CatalogDbContext catalog = new(fixture.Path);
        var route = CapsuleerTrafficRoutes.Journey(catalog, FirstStation, kind, 0, 0, HullClass.Hauler)!;
        Assert.NotNull(route);
        Assert.InRange(route.Systems.Length - 1, minimum, maximum);
        Assert.Equal(route.Systems.Length, route.Systems.Distinct().Count());
        AssertPathsAreReal(catalog, new(1, 1, [route]), FirstSystem);
        using var metrics = SessionPerformance.Begin("warm journey destinations", _ => { }, force: true)!;
        Assert.Equal(route, CapsuleerTrafficRoutes.Journey(catalog, FirstStation, kind, 0, 0, HullClass.Hauler));
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 10)]
    [InlineData(2, 3)]
    [InlineData(3, 10)]
    public void GateVisitorsContinuePastAStationlessCorridorToDifferentStations(int visitor, int jumps)
    {
        using Fixture fixture = new();
        fixture.AddStation(FirstStation, FirstSystem);
        fixture.AddStation(FirstStation + 1, FirstSystem + 10);
        fixture.AddStation(FirstStation + 2, FirstSystem + 3);
        for (int offset = 0; offset < 10; offset++)
        {
            fixture.Connect(FirstSystem + offset, FirstSystem + offset + 1);
        }
        using CatalogDbContext catalog = new(fixture.Path);
        var gate = catalog.Definitions.GatesInSystem(FirstSystem + 1).Single(g => g.DestinationSystemId == FirstSystem + 2);
        var route = CapsuleerTrafficRoutes.Inbound(
            catalog,
            new(FirstSystem + 2, FirstSystem + 1, gate.DestinationId, gate.Id),
            visitor: visitor
        )!;
        Assert.NotNull(route);
        Assert.NotEqual(route.SourceStationId, route.DestinationStationId);
        Assert.Equal(Enumerable.Range(FirstSystem, jumps + 1), route.Systems);
        AssertPathsAreReal(catalog, new(2, 0, [route]), FirstSystem + 2);
    }

    [Fact]
    public void InstalledVattuolenGateHasAnOnwardStationDestination()
    {
        string? path = Environment.GetEnvironmentVariable("EDEN_ENCOUNTER_CATALOG");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        using CatalogDbContext catalog = new(path);
        const int vattuolen = 30000173;
        const long gateId = 50005333;
        var gate = catalog.Definitions.Gates[(int)gateId];
        var route = CapsuleerTrafficRoutes.Inbound(catalog, new(gate.DestinationSystemId, vattuolen, gate.DestinationId, gateId))!;
        Assert.NotNull(route);
        Assert.NotEqual(route.SourceStationId, route.DestinationStationId);
        int crossing = Array.IndexOf(route.Systems, gate.DestinationSystemId);
        Assert.True(crossing > 0 && crossing < route.Systems.Length - 1);
        Assert.NotEqual(vattuolen, route.Systems[crossing + 1]);
        AssertPathsAreReal(catalog, new(2, 1, [route]), vattuolen);
    }

    [Fact]
    public void BusyHomeRostersAlwaysIncludeLocalsRegionalAndLongDistanceTravelers()
    {
        var roles = Enumerable.Range(0, 5).Select(slot => TrafficJourneyRules.Kind(slot, busy: true)).ToArray();
        Assert.Equal(2, roles.Count(kind => kind == TrafficJourneyKind.Local));
        Assert.Contains(TrafficJourneyKind.Regional, roles);
        Assert.Contains(TrafficJourneyKind.LongDistance, roles);
        Assert.Equal(TrafficJourneyKind.Local, TrafficJourneyRules.Kind(0, busy: false));
    }

    [Fact]
    public void LocalTripsPreferHomeSystemStationsAndHomewardRoutesUseRealGates()
    {
        using Fixture fixture = new();
        fixture.AddStation(FirstStation, FirstSystem);
        fixture.AddStation(FirstStation + 1, FirstSystem);
        fixture.AddStation(FirstStation + 2, FirstSystem + 1);
        fixture.Connect(FirstSystem, FirstSystem + 1);
        using CatalogDbContext catalog = new(fixture.Path);
        var local = CapsuleerTrafficRoutes.Journey(catalog, FirstStation, TrafficJourneyKind.Local, 1234, 0, HullClass.Hauler)!;
        Assert.Equal([FirstSystem], local.Systems);
        var homeward = CapsuleerTrafficRoutes.ToStation(catalog, FirstStation + 2, FirstStation, HullClass.Hauler)!;
        Assert.Equal([FirstSystem + 1, FirstSystem], homeward.Systems);
        Assert.Equal(FirstStation, homeward.DestinationStationId);
        Assert.Null(CapsuleerTrafficRoutes.ToStation(catalog, FirstStation + 2, FirstStation, HullClass.Titan));
    }

    private static void AssertPathsAreReal(CatalogDbContext catalog, CapsuleerTrafficPlan plan, int systemId)
    {
        foreach (var route in plan.Routes)
        {
            Assert.Contains(systemId, route.Systems);
            Assert.Equal(catalog.Definitions.Stations[route.SourceStationId].SolarSystemId, route.Systems[0]);
            if (route.SourceStationId != route.DestinationStationId)
            {
                Assert.Equal(catalog.Definitions.Stations[route.DestinationStationId].SolarSystemId, route.Systems[^1]);
            }
            foreach (var (source, destination) in route.Systems.Zip(route.Systems.Skip(1)))
            {
                Assert.Contains(
                    catalog.Definitions.GatesInSystem(source),
                    gate =>
                        gate.DestinationSystemId == destination
                        && catalog.Definitions.Gates.TryGetValue(gate.DestinationId, out var other)
                        && other.DestinationId == gate.Id
                );
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = TestDirectory.Create("capsuleer-traffic-routes-");
        private int _nextGate = 50010000;
        internal string Path { get; }

        internal Fixture()
        {
            Path = System.IO.Path.Combine(_directory.FullName, "catalog.sqlite");
            TestCatalog.Create(Path);
            using CatalogDbContext catalog = new(Path, writable: true);
            catalog.Database.ExecuteSqlRaw("DELETE FROM Stargate; DELETE FROM Station;");
            for (int offset = 0; offset <= 10; offset++)
            {
                int system = FirstSystem + offset;
                catalog.Database.ExecuteSql($"INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES ({system},20000020,'Traffic')");
                // The route graph deliberately crosses high, low and null security.
                string content = (offset % 3) switch
                {
                    0 => "{\"security\":1.0}",
                    1 => "{\"security\":0.2}",
                    _ => "{\"security\":-0.8}",
                };
                catalog.Database.ExecuteSql(
                    $"INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('client/solarsystemcontent',{system},{content})"
                );
            }
        }

        internal void AddStation(int station, int system)
        {
            using CatalogDbContext catalog = new(Path, writable: true);
            catalog.Database.ExecuteSql(
                $"""
                INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                VALUES ({station},52678,1000035,{system},14,40009087,1,2,3)
                """
            );
        }

        internal void Connect(int source, int destination)
        {
            int outgoing = _nextGate++;
            int incoming = _nextGate++;
            using CatalogDbContext catalog = new(Path, writable: true);
            catalog.Database.ExecuteSql(
                $"""
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z) VALUES
                    ({outgoing},{source},16,10,1,2,3),({incoming},{destination},16,10,4,5,6);
                INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES
                    ({outgoing},{source},{incoming}),({incoming},{destination},{outgoing});
                """
            );
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
