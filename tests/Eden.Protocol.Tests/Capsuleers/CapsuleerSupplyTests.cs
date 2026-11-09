using Eden.Data.World.Capsuleers;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerSupplyTests
{
    [Fact]
    public void SuppliesPreserveCargoAndRejectADifferentOriginalShipWithoutAMarker()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        var founder = f.Founder();
        using WorldDbContext world = new(f.Database.Path);
        using CatalogDbContext catalog = new(f.Database.CatalogPath);
        using var transaction = world.Database.BeginTransaction();
        TrafficFitting.Apply(
            world,
            catalog,
            founder.PilotId,
            founder.FounderShipId,
            TrafficSnapshotTests.Profile() with
            {
                HullTypeId = 603,
                Equipment = [new(561, (int)InventoryFlag.HighSlot0, 1), new(215, (int)InventoryFlag.HighSlot0, 50)],
            },
            f.Clock.Now
        );
        CapsuleerSupplyStore.Ensure(world, catalog, founder.PilotId, founder.FounderShipId + 1);
        Assert.Empty(world.Set<SavedCapsuleerSupplies>());
        long cargo = InventoryPersistence.NextId(world);
        double volume = PilotInventoryStore.Volume(catalog, 215);
        double capacity = PilotFittingStore.ShipAttributes(world, catalog, founder.PilotId)[DogmaAttributeId.CargoCapacity];
        int existing = (int)Math.Floor(capacity / volume) - 2;
        world.Add(
            new OwnedItem
            {
                Id = cargo,
                OwnerId = founder.PilotId,
                LocationId = founder.FounderShipId,
                TypeId = 215,
                Quantity = existing,
                FlagId = (int)InventoryFlag.Cargo,
            }
        );
        world.SaveChanges();
        CapsuleerSupplyStore.Ensure(world, catalog, founder.PilotId, founder.FounderShipId);
        Assert.Equal(existing, world.Set<OwnedItem>().Find(cargo)!.Quantity);
        Assert.InRange(
            world
                .Set<OwnedItem>()
                .Where(item =>
                    item.OwnerId == founder.PilotId
                    && item.LocationId == founder.FounderShipId
                    && item.FlagId == (int)InventoryFlag.Cargo
                    && item.Id != cargo
                )
                .Sum(item => item.Quantity),
            1,
            2
        );
        Assert.True(
            InventoryPersistence.UsedVolume(world, catalog, founder.PilotId, founder.FounderShipId, (int)InventoryFlag.Cargo) <= capacity
        );
        transaction.Commit();
    }

    [Fact]
    public void SpareChargesAreBoundedAtomicAndNeverReplenishedAfterConsumptionOrReopen()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        var founder = f.Founder();
        using (WorldDbContext world = new(f.Database.Path))
        using (CatalogDbContext catalog = new(f.Database.CatalogPath))
        using (var transaction = world.Database.BeginTransaction())
        {
            var profile = TrafficSnapshotTests.Profile() with
            {
                HullTypeId = 603,
                Equipment = [new(561, (int)InventoryFlag.HighSlot0, 1), new(215, (int)InventoryFlag.HighSlot0, 50)],
            };
            TrafficFitting.Apply(world, catalog, founder.PilotId, founder.FounderShipId, profile, f.Clock.Now);
            transaction.Commit();
        }
        using (WorldDbContext world = new(f.Database.Path))
        using (CatalogDbContext catalog = new(f.Database.CatalogPath))
        using (var transaction = world.Database.BeginTransaction())
        {
            CapsuleerSupplyStore.Ensure(world, catalog, founder.PilotId, founder.FounderShipId);
            Assert.Single(world.Set<SavedCapsuleerSupplies>());
            Assert.NotEmpty(
                world.Set<OwnedItem>().Where(item => item.LocationId == founder.FounderShipId && item.FlagId == (int)InventoryFlag.Cargo)
            );
            transaction.Rollback();
        }
        using (WorldDbContext world = new(f.Database.Path))
        using (CatalogDbContext catalog = new(f.Database.CatalogPath))
        using (var transaction = world.Database.BeginTransaction())
        {
            Assert.Empty(world.Set<SavedCapsuleerSupplies>());
            CapsuleerSupplyStore.Ensure(world, catalog, founder.PilotId, founder.FounderShipId);
            var spares = Assert.Single(
                world
                    .Set<OwnedItem>()
                    .Where(item =>
                        item.LocationId == founder.FounderShipId && item.FlagId == (int)InventoryFlag.Cargo && item.TypeId == 215
                    )
            );
            double volume = PilotInventoryStore.Volume(catalog, 215);
            int maximum = (int)Math.Floor(catalog.RequireItem(561).Capacity!.Value / volume) * 5;
            Assert.Equal(maximum, spares.Quantity);
            Assert.True(
                InventoryPersistence.UsedVolume(world, catalog, founder.PilotId, founder.FounderShipId, (int)InventoryFlag.Cargo)
                    <= PilotFittingStore.ShipAttributes(world, catalog, founder.PilotId)[DogmaAttributeId.CargoCapacity]
            );
            world.Remove(spares);
            world.SaveChanges();
            transaction.Commit();
        }
        WorldInitialization.Initialize(f.Database, f.Clock);
        using WorldDbContext reopened = new(f.Database.Path);
        using CatalogDbContext definitions = new(f.Database.CatalogPath);
        using var final = reopened.Database.BeginTransaction();
        CapsuleerSupplyStore.Ensure(reopened, definitions, founder.PilotId, founder.FounderShipId);
        Assert.Single(reopened.Set<SavedCapsuleerSupplies>());
        Assert.Empty(
            reopened.Set<OwnedItem>().Where(item => item.LocationId == founder.FounderShipId && item.FlagId == (int)InventoryFlag.Cargo)
        );
        final.Commit();
    }

    [Fact]
    public void DroneStarterFlightUsesTheLargestLegalMobileClassAndIsNeverRecreated()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        PassiveCatalogFixture.LoadCombatDrones(f.Database.CatalogPath);
        var founder = f.Founder();
        const int brutix = 16229;
        using (WorldDbContext world = new(f.Database.Path))
        using (CatalogDbContext catalog = new(f.Database.CatalogPath))
        using (var transaction = world.Database.BeginTransaction())
        {
            TrafficFitting.Apply(
                world,
                catalog,
                founder.PilotId,
                founder.FounderShipId,
                TrafficSnapshotTests.Profile() with
                {
                    HullTypeId = brutix,
                    Equipment = [],
                },
                f.Clock.Now
            );
            CapsuleerSupplyStore.Ensure(world, catalog, founder.PilotId, founder.FounderShipId);
            var flight = Assert.Single(
                world.Set<OwnedItem>().Where(item => item.LocationId == founder.FounderShipId && item.FlagId == (int)InventoryFlag.DroneBay)
            );
            var profile = DroneCatalog.Profile(catalog, flight.TypeId);
            Assert.Equal(CombatDroneKind.Medium, profile.Kind);
            Assert.Equal(5, flight.Quantity);
            var fit = FittingCalculation.Read(
                world,
                catalog,
                world.Pilots.Include(pilot => pilot.ActiveShip).Single(pilot => pilot.Id == founder.PilotId)
            );
            Assert.True(profile.Volume * flight.Quantity <= fit.Graph.Read(founder.FounderShipId, DogmaAttributeId.DroneCapacity));
            Assert.True(profile.Bandwidth * flight.Quantity <= fit.Graph.Read(founder.FounderShipId, DogmaAttributeId.DroneBandwidth));
            Assert.True(flight.Quantity <= DroneCatalog.ControlLimit(fit));
            world.Remove(flight);
            world.SaveChanges();
            transaction.Commit();
        }
        using WorldDbContext reopened = new(f.Database.Path);
        using CatalogDbContext definitions = new(f.Database.CatalogPath);
        using var final = reopened.Database.BeginTransaction();
        CapsuleerSupplyStore.Ensure(reopened, definitions, founder.PilotId, founder.FounderShipId);
        Assert.Empty(
            reopened.Set<OwnedItem>().Where(item => item.LocationId == founder.FounderShipId && item.FlagId == (int)InventoryFlag.DroneBay)
        );
        Assert.Single(reopened.Set<SavedCapsuleerSupplies>());
        final.Commit();
    }
}
