using Eden.Data.World.Capsuleers;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerInboundProvisioningTests
{
    private const int Observer = 30000141;
    private const int Source = 30000142;
    private const long ArrivalGate = 50009991;
    private const long DepartureGate = 50009992;
    private static readonly WatchedTrafficGate s_gate = new(Observer, Source, ArrivalGate, DepartureGate);

    [Fact]
    public void WatchedGateGetsOneDurableFarSidePilotWithBoundedDeadlineAndSharedReverseIdentity()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        var provisioner = new CapsuleerProvisioner(fixture.Database, fixture.Clock);
        long pilot = provisioner.EnsureInbound(s_gate)!.Value;
        string progress;
        long ship;
        long due;
        using (WorldDbContext world = new(fixture.Database.Path))
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
        {
            var agent = Assert.Single(world.Set<SavedCapsuleerAgent>());
            Assert.Equal(pilot, agent.PilotId);
            Assert.Equal(ArrivalGate, agent.Slot);
            Assert.Equal(Observer, agent.CommunitySystemId);
            var saved = Assert.Single(world.Set<SavedCapsuleerHauler>());
            ship = saved.FounderShipId;
            progress = saved.Progress!;
            var work = HaulerCheckpoint.Read(progress)!;
            Assert.Equal(HaulerActivity.Inbound, work.Activity);
            Assert.Equal(DepartureGate, work.TargetId);
            Assert.Equal(s_gate.ArrivalGateId, work.TrafficLane!.ArrivalGateId);
            Assert.Equal(Source, world.Positions.Single(position => position.ShipId == ship).SystemId);
            var route = CapsuleerTrafficFlight.Route(saved, catalog);
            Assert.Equal(Observer, CapsuleerTrafficFlight.Leg(route, work, Source).NextSystemId);
            Assert.Equal(saved.SourceStationId, saved.DestinationStationId);
            var job = Assert.Single(world.Set<SavedCapsuleerJob>());
            due = job.DueTicks;
            Assert.Equal(CapsuleerAction.GateTransfer, job.Action);
            Assert.Equal(work.DueTicks, due);
            Assert.InRange(due, TimeSpan.FromSeconds(1).Ticks, TimeSpan.FromMinutes(3).Ticks);
            Assert.Equal(HaulingRules.StartingCents, world.Set<PilotWallet>().Single(wallet => wallet.PilotId == pilot).Cents);
            Assert.Equal(100, world.Set<OwnedItem>().Single(item => item.LocationId == ship).Quantity);
        }
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        var reopened = new CapsuleerProvisioner(fixture.Database, fixture.Clock);
        Assert.Equal(pilot, reopened.EnsureInbound(s_gate));
        Assert.Equal(pilot, reopened.EnsureInbound(new(Source, Observer, DepartureGate, ArrivalGate), sourceObserved: true));
        using WorldDbContext savedWorld = new(fixture.Database.Path);
        Assert.Single(savedWorld.Set<SavedCapsuleerAgent>());
        var founder = Assert.Single(savedWorld.Set<SavedCapsuleerHauler>());
        Assert.Equal(ship, founder.FounderShipId);
        Assert.Equal(progress, founder.Progress);
        Assert.Equal(due, Assert.Single(savedWorld.Set<SavedCapsuleerJob>()).DueTicks);
        Assert.Equal(HaulingRules.StartingCents, savedWorld.Set<PilotWallet>().Single(wallet => wallet.PilotId == pilot).Cents);
        Assert.Equal(100, savedWorld.Set<OwnedItem>().Single(item => item.LocationId == ship).Quantity);
    }

    [Fact]
    public void AlreadyObservedSourceCreatesDockedOwnedShipWithoutOnGridAppearance()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        long pilot = new CapsuleerProvisioner(fixture.Database, fixture.Clock).EnsureInbound(s_gate, sourceObserved: true)!.Value;
        using WorldDbContext world = new(fixture.Database.Path);
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        var saved = world.Set<SavedCapsuleerHauler>().Single(hauler => hauler.PilotId == pilot);
        var ship = world.Ships.Single(item => item.Id == saved.FounderShipId);
        Assert.Equal(saved.SourceStationId, ship.StationId);
        Assert.Equal(Source, catalog.RequireStation(ship.StationId).SolarSystemId);
        Assert.DoesNotContain(world.Positions, position => position.ShipId == ship.Id);
        var work = HaulerCheckpoint.Read(saved.Progress)!;
        Assert.Equal(HaulerActivity.Docked, work.Activity);
        Assert.Equal(ArrivalGate, work.TrafficLane!.ArrivalGateId);
        var job = Assert.Single(world.Set<SavedCapsuleerJob>());
        Assert.Equal(CapsuleerAction.Trade, job.Action);
        Assert.InRange(job.DueTicks, 1, TimeSpan.FromSeconds(31).Ticks);
    }

    [Fact]
    public void StationlessObserverUsesItsRealGateAsTurnaround()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false);
        WatchedTrafficGate gate = new(30000144, Source, 50002185, 50001249);
        long pilot = new CapsuleerProvisioner(fixture.Database, fixture.Clock).EnsureInbound(gate)!.Value;
        using WorldDbContext world = new(fixture.Database.Path);
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        var saved = world.Set<SavedCapsuleerHauler>().Single(hauler => hauler.PilotId == pilot);
        Assert.Equal([Source, 30000144], saved.RouteSystems!);
        Assert.Equal(saved.SourceStationId, saved.DestinationStationId);
        var work = HaulerCheckpoint.Read(saved.Progress)!;
        Assert.Equal(gate.DepartureGateId, work.TargetId);
        var leg = CapsuleerTrafficFlight.Leg(CapsuleerTrafficFlight.Route(saved, catalog), work, Source);
        Assert.Equal(gate.ObserverSystemId, leg.NextSystemId);
    }

    [Fact]
    public void TwoWatchedStationlessSystemsDeferTheirFirstArrivalUntilTheFarSideEmpties()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false);
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true))
        {
            // Perimeter remains connected to Jita and supplies the only station for this lane.
            catalog.Database.ExecuteSqlRaw("UPDATE Station SET SolarSystemId=30000144");
        }
        var provisioner = new CapsuleerProvisioner(fixture.Database, fixture.Clock);
        Assert.Null(provisioner.EnsureInbound(s_gate, sourceObserved: true));
        using WorldDbContext saved = new(fixture.Database.Path);
        Assert.Empty(saved.Set<SavedCapsuleerAgent>());
        long pilot = provisioner.EnsureInbound(s_gate)!.Value;
        var ship = saved.Pilots.Single(p => p.Id == pilot).ActiveShipId;
        Assert.Equal(Source, saved.Positions.Single(p => p.ShipId == ship).SystemId);
        Assert.Equal(pilot, provisioner.EnsureInbound(s_gate, sourceObserved: true));
    }

    [Fact]
    public void ExistingPreparedPolicyAndOrdinaryShipsGainOneLaneWithoutReplacement()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        var provisioner = new CapsuleerProvisioner(fixture.Database, fixture.Clock);
        provisioner.EnsureSystem(Observer);
        long[] originalShips;
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            originalShips = world.Ships.Select(ship => ship.Id).ToArray();
            Assert.Equal(CapsuleerProvisioningRules.Version, world.Set<SavedCapsuleerSystem>().Single().PolicyVersion);
        }
        long pilot = provisioner.EnsureInbound(s_gate)!.Value;
        Assert.Equal(pilot, provisioner.EnsureInbound(s_gate));
        using WorldDbContext saved = new(fixture.Database.Path);
        Assert.All(originalShips, id => Assert.Contains(saved.Ships, ship => ship.Id == id));
        Assert.Equal(originalShips.Length + 1, saved.Ships.Count());
        Assert.Single(saved.Set<SavedCapsuleerAgent>(), agent => agent.Slot == ArrivalGate);
    }

    [Fact]
    public void InvalidExactGateDoesNotGrantAnything()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        using WorldDbContext world = new(fixture.Database.Path);
        int ships = world.Ships.Count();
        var provisioner = new CapsuleerProvisioner(fixture.Database, fixture.Clock);
        Assert.Null(provisioner.EnsureInbound(s_gate with { DepartureGateId = ArrivalGate }));
        Assert.Null(provisioner.EnsureInbound(s_gate with { SourceSystemId = 30000144 }));
        Assert.Null(provisioner.EnsureInbound(s_gate with { ArrivalGateId = long.MaxValue }));
        Assert.Equal(ships, world.Ships.Count());
        Assert.Empty(world.Set<SavedCapsuleerAgent>());
    }

    [Fact]
    public void QuietProfilesOnBothSidesKeepTheirLaneQuiet()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        const int quietSource = 30000143;
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql($"INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES ({quietSource},20000020,'Quiet')");
            catalog.Database.ExecuteSql($"UPDATE Stargate SET SolarSystemId={quietSource} WHERE Id={DepartureGate}");
            catalog.Database.ExecuteSql($"UPDATE Celestial SET SolarSystemId={quietSource} WHERE Id={DepartureGate}");
        }
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            world.Set<SavedCapsuleerProvisioning>().Single().WorldSeed = 2;
            world.SaveChanges();
        }
        Assert.Equal(0, CapsuleerEncounterRules.Haulers(2, Observer, 1, 1));
        Assert.Equal(0, CapsuleerEncounterRules.Haulers(2, quietSource, 1, 0));
        Assert.Null(new CapsuleerProvisioner(fixture.Database, fixture.Clock).EnsureInbound(s_gate with { SourceSystemId = quietSource }));
        using WorldDbContext saved = new(fixture.Database.Path);
        Assert.Empty(saved.Set<SavedCapsuleerAgent>());
        Assert.Empty(saved.Set<SavedCapsuleerHauler>());
    }

    [Fact]
    public void FailedLaneGrantRollsBackPilotShipWalletCargoAndPopulationTogether()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        int ships;
        int pilots;
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            ships = world.Ships.Count();
            pilots = world.Pilots.Count();
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectLane BEFORE INSERT ON WorldCapsuleerAgent BEGIN SELECT RAISE(ABORT,'lane grant rejected'); END"
            );
        }
        Assert.Throws<SqliteException>(() => new CapsuleerProvisioner(fixture.Database, fixture.Clock).EnsureInbound(s_gate));
        using WorldDbContext saved = new(fixture.Database.Path);
        Assert.Equal(ships, saved.Ships.Count());
        Assert.Equal(pilots, saved.Pilots.Count());
        Assert.Empty(saved.Set<SavedCapsuleerAgent>());
        Assert.Empty(saved.Set<SavedCapsuleerHauler>());
        Assert.Empty(saved.Set<SavedCapsuleerPopulation>());
    }

    [Fact]
    public void ActiveNeighborSuppliesLaneWhenCanonicalSideProfileIsQuiet()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            world.Set<SavedCapsuleerProvisioning>().Single().WorldSeed = 2;
            world.SaveChanges();
        }
        Assert.Equal(0, CapsuleerEncounterRules.Haulers(2, Observer, 1, 1));
        Assert.Equal(1, CapsuleerEncounterRules.Haulers(2, Source, 1, 1));
        Assert.NotNull(new CapsuleerProvisioner(fixture.Database, fixture.Clock).EnsureInbound(s_gate));
        using WorldDbContext saved = new(fixture.Database.Path);
        Assert.Single(saved.Set<SavedCapsuleerAgent>());
    }
}
