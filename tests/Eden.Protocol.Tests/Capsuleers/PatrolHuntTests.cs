using Eden.Data.World.Capsuleers;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class PatrolHuntTests
{
    private const int Home = 30000141;
    private const int Away = 30000142;
    private const int HomeStation = 60015157;
    private const int AwayStation = 60003760;
    private const int OutboundGate = 50009991;
    private const int InboundGate = 50009992;

    private const long LeadGrid = 40009991;

    [Fact]
    public void UnattendedHunterUsesAScanLeadToReachIndependentlyTravelingFreight()
    {
        using var fixture = new RoamingGangTests.Fixture(
            regional: true,
            patrolSeed: RoamingGangTests.Fixture.DestroyerSeed,
            soloHull: CapsuleerPatrolFit.Catalyst,
            unattended: true,
            additionalCatalogData: path =>
            {
                using var catalog = new CatalogDbContext(path, writable: true);
                for (long id = LeadGrid; id < LeadGrid + 5; id++)
                {
                    long x = 900_000_000_000 + (id - LeadGrid + 1) * 100_000_000;
                    catalog.Database.ExecuteSql(
                        $"""
                        INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z)
                        VALUES ({id},{Home},15,{AsteroidDefinitions.BeltGroup},0,{x},-100000000000,-1100000000000);
                        INSERT INTO SourceRecord VALUES ('client/asteroidcountbytypeidbeltid',{id},{"{}"});
                        """
                    );
                }
            }
        );
        using var catalog = new CatalogDbContext(fixture.Database.CatalogPath);
        long hunterId;
        long freightId;
        long freightShip;
        using (var action = fixture.Live.Economy.Begin())
        {
            hunterId = action
                .Set<SavedCapsuleerHauler>()
                .AsEnumerable()
                .Single(row => action.Set<SavedCapsuleerSuspension>().Find(row.PilotId) is null)
                .PilotId;
            var freightRow = action
                .Set<SavedCapsuleerHauler>()
                .Where(row => row.SourceStationId == HomeStation)
                .AsEnumerable()
                .First(row => action.Set<SavedCapsuleerSuspension>().Find(row.PilotId) is not null);
            freightId = freightRow.PilotId;
            var agent = action.Set<SavedCapsuleerAgent>().Find(freightId)!;
            agent.CommunitySystemId = Away;
            agent.Slot =
                action
                    .Set<SavedCapsuleerAgent>()
                    .Where(other => other.CommunitySystemId == Away && other.Career == agent.Career)
                    .Select(other => other.Slot)
                    .DefaultIfEmpty()
                    .Max() + 1;
            var freighter = action.Pilots.Find(freightId)!;
            var ship = freighter.ActiveShip;
            ship.StationId = HomeStation;
            ship.TypeId = HaulingRules.BadgerType;
            freightShip = ship.Id;
            action.Remove(action.Set<SavedCapsuleerSuspension>().Find(freightId)!);
            var gate = SystemObjects.Geometry(catalog, Home).Single(body => body.Id == OutboundGate);
            var position = gate.Position + new SpacePosition(0, 30_000, 0);
            SqliteWorld.SetPosition(action, catalog, ship.Id, position, Home, fixture.Clock.Now);
            long cargoId = InventoryPersistence.NextId(action);
            action.Add(
                new OwnedItem
                {
                    Id = cargoId,
                    OwnerId = freightId,
                    LocationId = ship.Id,
                    TypeId = 215,
                    Quantity = 10,
                    FlagId = (int)InventoryFlag.Cargo,
                }
            );
            freightRow.DestinationStationId = AwayStation;
            freightRow.RouteSystems = [Home, Away];
            var work = new HaulerProgress(HaulerActivity.Waiting, 0, 0, ship.Id, AwayStation, OutboundGate, new NavigationState().Capture())
            {
                Journey = new(TrafficJourneyKind.Regional, HomeStation, HomeStation, AwayStation, [Home, Away], 1),
                Freight = new(1, AwayStation, [new(cargoId, 215, 10)]),
            };
            new HaulerCheckpoint(freightId, work).Apply(action);
            int group = catalog.RequireSystem(Home).ConstellationId;
            RegionalConflictStore.Report(
                action,
                catalog,
                HomeStation,
                new(group, null, 99, 98, Home, LeadGrid, RegionalReportKind.Freight, 0, RegionalConflictRules.ReportTicks)
            );
            action.Commit();
            fixture.Live.HaulerCheckpoints[freightId] = new(freightId, work);
        }
        fixture.Scheduler.PopulationPrepared(Away, freightId);

        bool searchedLead = false;
        bool redirected = false;
        bool reachedSearchGrid = false;
        bool engaged = false;
        bool freightEscaped = false;
        for (int turn = 0; turn < 450 && !engaged && !freightEscaped; turn++)
        {
            fixture.Tick();
            var hunter = fixture.Scheduler.Hauler(hunterId);
            if (hunter?.Work.Ratting is { Patrol.Kind: PatrolKind.Roam } patrol)
            {
                if (!searchedLead && hunter.Work.TargetId == LeadGrid && patrol.Phase == RattingPhase.Searching)
                {
                    searchedLead = true;
                    Assert.NotEqual(OutboundGate, patrol.Belts[1]);
                }
                redirected |= searchedLead && hunter.Work.TargetId == OutboundGate && patrol.BeltIndex == 1;
                reachedSearchGrid |= redirected && hunter.Work.TargetId == OutboundGate && patrol.Phase == RattingPhase.Searching;
                engaged = patrol.TargetId == freightShip;
                if (engaged)
                {
                    Assert.True(redirected);
                    Assert.Equal(OutboundGate, hunter.Work.TargetId);
                }
            }
            freightEscaped = fixture.Scheduler.Hauler(freightId)?.Pilot.SystemId == Away;
        }
        Assert.True(searchedLead);
        Assert.True(redirected);
        Assert.True(reachedSearchGrid || engaged);

        Assert.Equal(0, fixture.Scheduler.Read().Failures);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnattendedLoadedFreightRespondsToFreshWarningAcrossItsGate(bool fresh)
    {
        using var fixture = new RoamingGangTests.Fixture(
            regional: true,
            patrolSeed: RoamingGangTests.Fixture.DestroyerSeed,
            soloHull: CapsuleerPatrolFit.Catalyst,
            unattended: true
        );
        using var catalog = new CatalogDbContext(fixture.Database.CatalogPath);
        long pilotId;
        long cargoId;
        using (var action = fixture.Live.Economy.Begin())
        {
            var row = action
                .Set<SavedCapsuleerHauler>()
                .Where(row => row.SourceStationId == HomeStation)
                .AsEnumerable()
                .First(row => action.Set<SavedCapsuleerSuspension>().Find(row.PilotId) is not null);
            pilotId = row.PilotId;
            var pilot = action.Pilots.Find(pilotId)!;
            var ship = pilot.ActiveShip;
            ship.StationId = HomeStation;
            ship.TypeId = HaulingRules.BadgerType;
            action.Remove(action.Set<SavedCapsuleerSuspension>().Find(pilotId)!);
            cargoId = InventoryPersistence.NextId(action);
            action.Add(
                new OwnedItem
                {
                    Id = cargoId,
                    OwnerId = pilotId,
                    LocationId = ship.Id,
                    TypeId = 215,
                    Quantity = 10,
                    FlagId = (int)InventoryFlag.Cargo,
                }
            );
            var work = new HaulerProgress(HaulerActivity.Docked, 0, 0, ship.Id, AwayStation, 0, new NavigationState().Capture())
            {
                Journey = new(TrafficJourneyKind.Regional, HomeStation, HomeStation, AwayStation, [Home, Away], 1),
                Freight = new(1, AwayStation, [new(cargoId, 215, 10)]),
            };
            row.DestinationStationId = AwayStation;
            row.RouteSystems = [Home, Away];
            new HaulerCheckpoint(pilotId, work).Apply(action);

            int group = catalog.RequireSystem(Home).ConstellationId;
            RegionalConflictStore.Report(
                action,
                catalog,
                HomeStation,
                new(group, null, 99, 98, Away, InboundGate, RegionalReportKind.Attack, 0, fresh ? TimeSpan.TicksPerHour : 0)
            );
            action.Commit();
            fixture.Live.HaulerCheckpoints[pilotId] = new(pilotId, work);
        }
        fixture.Scheduler.PopulationPrepared(Home, pilotId);

        bool undocked = false;
        bool reachedGate = false;
        bool reachedDestination = false;
        bool returned = false;
        for (int turn = 0; turn < 900 && !reachedDestination && !returned; turn++)
        {
            fixture.Tick();
            if (fixture.Scheduler.Hauler(pilotId) is not { } freight)
            {
                continue;
            }
            undocked |= freight.Pilot.Position is not null;
            reachedGate |= freight.Work.TargetId == OutboundGate;
            reachedDestination |= freight.Pilot.SystemId == Away;
            returned |= reachedGate && freight.Work.Retreating;
        }
        Assert.True(undocked);
        Assert.True(reachedGate);
        Assert.Equal(!fresh, reachedDestination);
        Assert.Equal(fresh, returned);
        Assert.NotNull(fixture.Live.Economy.Read(world => world.Set<OwnedItem>().Find(cargoId)));
        Assert.Equal(0, fixture.Scheduler.Read().Failures);
    }
}
