using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Insurance;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Market;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Insurance;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

internal static class HomeStationTestData
{
    internal const long PilotId = 90000001;
    internal const int Origin = 60003760;
    internal const int School = 60004420;
    internal const int Other = 60004421;
    internal const int System = 30001407;

    internal static void Prepare(TestWorld db)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        var origin = catalog.Set<Station>().Find(Origin)!;
        catalog.Add(
            new SolarSystem
            {
                Id = System,
                Name = "HQ system",
                ConstellationId = 20000020,
            }
        );
        foreach (int station in new[] { School, Other })
        {
            catalog.Add(
                new Station
                {
                    Id = station,
                    TypeId = origin.TypeId,
                    OwnerId = origin.OwnerId,
                    SolarSystemId = System,
                    OperationId = origin.OperationId,
                    OrbitId = origin.OrbitId,
                    X = station,
                    Y = 2,
                    Z = 3,
                }
            );
        }
        Add("client/schools", 19, new { corporationID = 1000044 });
        Add(
            "client/npccorporations",
            1000044,
            new
            {
                stationID = School,
                solarSystemID = System,
                tickerName = "SAK",
                ceoID = 3004181,
                factionID = 500001,
                taxRate = 0.11,
                shares = 100000000,
                memberLimit = -1,
            }
        );
        catalog.Add(
            new ItemGroup
            {
                Id = FleetDefinitions.CapsuleGroup,
                Name = "Capsule",
                CategoryId = 6,
            }
        );
        catalog.Add(
            new ItemType
            {
                Id = FleetDefinitions.CapsuleType,
                Name = "Capsule",
                GroupId = FleetDefinitions.CapsuleGroup,
                Capacity = 0,
                Mass = 32000,
                Radius = 2,
            }
        );
        Add("client/types", FleetDefinitions.CapsuleType, new { volume = 1000, published = 1 });
        catalog.SaveChanges();
        catalog.Database.ExecuteSqlRaw("INSERT INTO TypeAttribute SELECT 670,AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=601");
        InsuranceTestData.Fund(db, 50_000_000);
        return;

        void Add(string dataset, int key, object value) =>
            catalog.Add(
                new SourceRecord
                {
                    Dataset = dataset,
                    Key = key.ToString(),
                    Data = JsonSerializer.Serialize(value),
                }
            );
    }

    internal static long MakeActiveCapsule(TestWorld db, int station = Other)
    {
        using WorldDbContext world = new(db.Path);
        long id = InventoryPersistence.NextId(world);
        var ship = world
            .Add(
                new Ship
                {
                    Id = id,
                    OwnerId = PilotId,
                    TypeId = FleetDefinitions.CapsuleType,
                    StationId = station,
                    Name = "Old capsule",
                }
            )
            .Entity;
        world.Pilots.Find(PilotId)!.ActiveShip = ship;
        world.SaveChanges();
        return id;
    }

    internal static void Travel(TestWorld db, int station)
    {
        var pilot = db.Store.FindPilot(1000001)!;
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        var destination = catalog.Set<Station>().Find(station)!;
        db.Store.SetPosition(pilot.ShipId, new(destination.X, destination.Y, destination.Z), destination.SolarSystemId);
        db.Store.Dock(PilotId, pilot.ShipId, station);
    }
}

public sealed class HomeStationTests
{
    [Fact]
    public void TravelNeverChangesSavedHomeAndPaidSelectionsPersistIndependentlyOfTheActiveShip()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        HomeStationTestData.Prepare(db);
        var homes = db.Services.GetRequiredService<IPilotHomeStation>();
        Assert.Equal(HomeStationTestData.Origin, homes.Read(HomeStationTestData.PilotId).Id);
        var candidates = homes.Candidates(HomeStationTestData.PilotId);
        Assert.Equal(2, candidates.Count);
        Assert.True(candidates.Single(c => c.Station.Id == HomeStationTestData.School).School);
        Assert.Equal(HomeStationError.AlreadyHome, Assert.Single(candidates.Single(c => c.Current).Errors));
        HomeStationTestData.Travel(db, HomeStationTestData.Other);
        Assert.Equal(HomeStationTestData.Origin, homes.Read(HomeStationTestData.PilotId).Id);
        Assert.Equal(40_000_000, homes.Set(HomeStationTestData.PilotId, HomeStationTestData.Other, false).BalanceCents);
        Assert.Throws<InvalidDataException>(() => homes.Set(HomeStationTestData.PilotId, HomeStationTestData.Other, true));
        Assert.Equal(40_000_000, db.Services.GetRequiredService<IPilotWalletReader>().Balance(HomeStationTestData.PilotId));
        var pilot = db.Store.FindPilot(1000001)!;
        db.Store.SetPosition(pilot.ShipId, new(1, 2, 3));
        Assert.DoesNotContain(homes.Candidates(pilot.PilotId), candidate => candidate.Current);
        Assert.Equal(30_000_000, homes.Set(pilot.PilotId, HomeStationTestData.School, false).BalanceCents);
        Assert.Null(homes.NextRemoteChange(pilot.PilotId));
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path), clock);
        Assert.Equal(HomeStationTestData.School, reopened.GetRequiredService<IPilotHomeStation>().Read(pilot.PilotId).Id);
        Assert.Equal(HomeStationTestData.Origin, homes.Read(90000002).Id);
        var payments = reopened.GetRequiredService<IPilotWalletReader>().Journal(pilot.PilotId).HomeStationPayments;
        Assert.Equal(2, payments.Count);
        Assert.All(payments, payment => Assert.Equal(10_000_000, payment.AmountCents));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmationNeverAuthorizesArbitraryRemoteStationsAndInsufficientFundsDoNotMutateState(bool confirmation)
    {
        using TestWorld db = new();
        HomeStationTestData.Prepare(db);
        var homes = db.Services.GetRequiredService<IPilotHomeStation>();
        Assert.Throws<InvalidDataException>(() => homes.Set(HomeStationTestData.PilotId, HomeStationTestData.Other, confirmation));
        Assert.Throws<InvalidDataException>(() => homes.Set(HomeStationTestData.PilotId, int.MaxValue, confirmation));
        InsuranceTestData.Fund(db, HomeStationRules.RelocationFeeCents - 1);
        Assert.Throws<InvalidDataException>(() => homes.Set(HomeStationTestData.PilotId, HomeStationTestData.School, confirmation));
        Assert.Equal(
            HomeStationRules.RelocationFeeCents - 1,
            db.Services.GetRequiredService<IPilotWalletReader>().Balance(HomeStationTestData.PilotId)
        );
        Assert.Equal(HomeStationTestData.Origin, homes.Read(HomeStationTestData.PilotId).Id);
        Assert.Empty(db.Services.GetRequiredService<IPilotWalletReader>().Journal(HomeStationTestData.PilotId).HomeStationPayments);
    }

    [Fact]
    public void InvalidSavedDestinationFallsBackToSchoolHeadquartersAndSelectingCurrentRepairsIt()
    {
        using TestWorld db = new();
        HomeStationTestData.Prepare(db);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<SavedHomeStation>().Find(HomeStationTestData.PilotId)!.StationId = int.MaxValue;
            world.SaveChanges();
        }
        var homes = db.Services.GetRequiredService<IPilotHomeStation>();
        Assert.Equal(
            new(HomeStationTestData.School, db.Store.FindPilot(1000001)!.StationTypeId, HomeStationTestData.System, true),
            homes.Read(HomeStationTestData.PilotId)
        );
        homes.Set(HomeStationTestData.PilotId, HomeStationTestData.Origin, false);
        Assert.False(homes.Read(HomeStationTestData.PilotId).IsFallback);
    }

    [Fact]
    public void Version44MigrationBackfillsOnceFromLastDockedStationWithoutResettingWalletOrTaskProgress()
    {
        using TestWorld db = new();
        HomeStationTestData.Prepare(db);
        HomeStationTestData.Travel(db, HomeStationTestData.Other);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "DROP TABLE WorldHomeCloneTransfer; DROP TABLE WorldHomeStationPayment; DROP TABLE WorldHomeStation; PRAGMA user_version=44"
            );
        }
        WorldInitialization.Open(db.Path);
        HomeStationTestData.Travel(db, HomeStationTestData.Origin);
        WorldInitialization.Open(db.Path);
        Assert.Equal(HomeStationTestData.Other, db.Services.GetRequiredService<IPilotHomeStation>().Read(HomeStationTestData.PilotId).Id);
        Assert.Equal(50_000_000, db.Services.GetRequiredService<IPilotWalletReader>().Balance(HomeStationTestData.PilotId));
        Assert.Equal(2, TaskTrackingTestData.Count(db, TaskEvents.Docked));
    }

    [Fact]
    public async Task ConcurrentCapsuleDestructionCreatesOneReplacementAtSavedHomeAndRetainsOtherAssetsAndWallet()
    {
        using TestWorld db = new();
        HomeStationTestData.Prepare(db);
        long old = HomeStationTestData.MakeActiveCapsule(db);
        long originalPoints;
        using (WorldDbContext before = new(db.Path))
        {
            originalPoints = before.Set<PilotSkill>().Find(HomeStationTestData.PilotId, 3327)!.Points;
        }
        var homes = db.Services.GetRequiredService<IPilotHomeStation>();
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        try
                        {
                            return homes.SelfDestruct(HomeStationTestData.PilotId, old);
                        }
                        catch (InvalidDataException)
                        {
                            return null;
                        }
                    })
                )
        );
        var transfer = Assert.Single(results, result => result is not null)!;
        Assert.NotEqual(old, transfer.Pilot.ShipId);
        Assert.Equal(HomeStationTestData.Origin, transfer.Pilot.StationId);
        Assert.Null(transfer.Pilot.Position);
        Assert.Equal(FleetDefinitions.CapsuleType, transfer.Pilot.ShipTypeId);
        Assert.Equal(2, transfer.Changes.Count);
        WorldInitialization.Open(db.Path);
        Assert.Equal(transfer.Pilot.ShipId, db.Store.FindPilot(1000001)!.ShipId);
        Assert.Equal(50_000_000, db.Services.GetRequiredService<IPilotWalletReader>().Balance(HomeStationTestData.PilotId));
        Assert.Equal(1, TaskTrackingTestData.Count(db, TaskEvents.PodLost));
        using WorldDbContext world = new(db.Path);
        Assert.Null(world.Ships.Find(old));
        Assert.NotNull(world.Ships.Find(1000000000001L));
        Assert.Single(world.Set<SavedHomeCloneTransfer>());
        Assert.Empty(world.Set<ShipLoss>());
        Assert.Empty(world.Set<SavedInsuranceLedger>());
        Assert.Equal(originalPoints, world.Set<PilotSkill>().Find(HomeStationTestData.PilotId, 3327)!.Points);
    }

    [Fact]
    public void RejectionsAndAuditFailureCannotDestroyOrMoveCapsules()
    {
        using TestWorld db = new();
        HomeStationTestData.Prepare(db);
        var homes = db.Services.GetRequiredService<IPilotHomeStation>();
        var pilot = db.Store.FindPilot(1000001)!;
        Assert.Throws<InvalidDataException>(() => homes.SelfDestruct(pilot.PilotId, pilot.ShipId));
        long old = HomeStationTestData.MakeActiveCapsule(db, HomeStationTestData.Origin);
        Assert.Throws<InvalidDataException>(() => homes.SelfDestruct(pilot.PilotId, old));
        using (WorldDbContext world = new(db.Path))
        {
            world.Ships.Find(old)!.StationId = HomeStationTestData.Other;
            world.SaveChanges();
        }
        db.Store.SetPosition(old, SpacePosition.Zero);
        Assert.Throws<InvalidDataException>(() => homes.SelfDestruct(pilot.PilotId, old));
        db.Store.SetPosition(old, null);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectClone BEFORE INSERT ON WorldHomeCloneTransfer BEGIN SELECT RAISE(ABORT, 'forced failure'); END"
            );
        }
        Assert.Throws<DbUpdateException>(() => homes.SelfDestruct(pilot.PilotId, old));
        Assert.Equal(old, db.Store.FindPilot(1000001)!.ShipId);
        Assert.Equal(HomeStationTestData.Other, db.Store.FindPilot(1000001)!.StationId);
        Assert.Equal(0, TaskTrackingTestData.Count(db, TaskEvents.PodLost));
        using WorldDbContext saved = new(db.Path);
        Assert.Empty(saved.Set<SavedHomeCloneTransfer>());
        Assert.Equal(3, saved.Ships.Count());
    }

    [Theory]
    [InlineData(31000001, 11000001, HomeStationError.Wormhole)]
    [InlineData(30000142, 10000070, HomeStationError.Triglavian)]
    public void ForbiddenRegionsProduceNativeCandidateErrorCodes(int system, int region, HomeStationError expected)
    {
        Assert.Equal(expected, Assert.Single(HomeStationRules.Errors(60004420, 60003760, system, region)));
    }

    [Fact]
    public async Task ConcurrentHomeChangesChargeOnceAndPaymentFailureRollsBackHomeAndWalletTogether()
    {
        using TestWorld db = new();
        HomeStationTestData.Prepare(db);
        var homes = db.Services.GetRequiredService<IPilotHomeStation>();
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectHomeFee BEFORE INSERT ON WorldHomeStationPayment BEGIN SELECT RAISE(ABORT, 'forced failure'); END"
            );
        }
        Assert.Throws<DbUpdateException>(() => homes.Set(HomeStationTestData.PilotId, HomeStationTestData.School, false));
        Assert.Equal(HomeStationTestData.Origin, homes.Read(HomeStationTestData.PilotId).Id);
        Assert.Equal(50_000_000, db.Services.GetRequiredService<IPilotWalletReader>().Balance(HomeStationTestData.PilotId));
        Assert.Empty(db.Services.GetRequiredService<IPilotWalletReader>().Journal(HomeStationTestData.PilotId).HomeStationPayments);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectHomeFee");
        }
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        try
                        {
                            return homes.Set(HomeStationTestData.PilotId, HomeStationTestData.School, false);
                        }
                        catch (InvalidDataException)
                        {
                            return null;
                        }
                    })
                )
        );
        Assert.Single(results, result => result is not null);
        Assert.Equal(40_000_000, db.Services.GetRequiredService<IPilotWalletReader>().Balance(HomeStationTestData.PilotId));
        Assert.Single(db.Services.GetRequiredService<IPilotWalletReader>().Journal(HomeStationTestData.PilotId).HomeStationPayments);
    }
}
