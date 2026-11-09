using Eden.Data.Catalog.Capsuleers;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class TrafficSnapshotTests
{
    private const int Source = 30000141;

    [Fact]
    public void OptInInstalledSnapshotMatchesManifestAndSupportedHullCatalog()
    {
        string? path = Environment.GetEnvironmentVariable("EDEN_TRAFFIC_SNAPSHOT");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        using CatalogDbContext catalog = new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "catalog.sqlite"));
        var snapshot = TrafficSnapshot.Read(path, TrafficSnapshot.Fingerprint(catalog));
        var metadata = Assert.IsType<TrafficSnapshotManifest>(snapshot.Manifest);
        Assert.NotEmpty(snapshot.Profiles);
        Assert.Equal(metadata.Accepted, snapshot.Profiles.Count);
        Assert.Equal(metadata.Pilots, snapshot.Profiles.Select(profile => profile.CharacterId).Distinct().Count());
        Assert.Equal(metadata.Hulls, snapshot.Profiles.Select(profile => profile.HullTypeId).Distinct().Count());
        Assert.Equal(metadata.Fits, snapshot.Profiles.Select(TrafficSnapshot.Signature).Distinct().Count());
        Assert.All(
            snapshot.Profiles,
            profile =>
                Assert.Equal(
                    Eden.Gameplay.Characters.CharacterNameResult.Available,
                    Eden.Gameplay.Characters.CharacterRules.ValidateName(profile.Name, false)
                )
        );
        foreach (int typeId in snapshot.Profiles.Select(profile => profile.HullTypeId).Distinct())
        {
            var type = catalog.RequireItem(typeId);
            Assert.True(HullCatalog.CanAssemble(catalog, type, type.CategoryId));
        }
    }

    [Fact]
    public void DeterministicCompleteFitProvisioningKeepsRealIdentityEquipmentAndNoStarterCargo()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        Install(fixture.Database, Profile());
        var provisioner = new CapsuleerProvisioner(fixture.Database, fixture.Clock);
        provisioner.EnsureSystem(Source);
        using WorldDbContext world = new(fixture.Database.Path);
        var identity = Assert.Single(world.Set<SavedTrafficIdentity>());
        var pilot = world.Pilots.Include(value => value.ActiveShip).Single(value => value.Id == identity.PilotId);
        Assert.Equal("Real Traffic Pilot", pilot.Name);
        Assert.Equal(648, pilot.ActiveShip.TypeId);
        var fitted = Assert.Single(world.Set<OwnedItem>().Where(value => value.LocationId == pilot.ActiveShipId));
        Assert.Equal(1317, fitted.TypeId);
        Assert.Equal((int)InventoryFlag.LowSlot0, fitted.FlagId);
        Assert.True(fitted.Online);
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
        {
            var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot.Id);
            Assert.True(attributes[DogmaAttributeId.CargoCapacity] > catalog.RequireItem(648).Capacity);
        }
        Assert.Equal(900000001, identity.CharacterId);
        Assert.Equal(123456789, identity.KillId);
        Assert.Equal(0, provisioner.EnsureSystem(Source).CreatedCommunities);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using WorldDbContext reopened = new(fixture.Database.Path);
        Assert.Equal(identity.CharacterId, Assert.Single(reopened.Set<SavedTrafficIdentity>()).CharacterId);
        Assert.Equal(fitted.Id, Assert.Single(reopened.Set<OwnedItem>().Where(value => value.LocationId == pilot.ActiveShipId)).Id);
    }

    [Fact]
    public void CompleteFitValidatorUsesIsolatedWorldAndRejectsEveryUnsupportedComponent()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        using TrafficFitValidator validator = new(fixture.Database.CatalogPath);
        Assert.Null(validator.Validate(Profile()));
        Assert.Null(validator.Validate(Profile()));
        Assert.NotNull(validator.Validate(Profile() with { HullTypeId = int.MaxValue }));
        Assert.NotNull(validator.Validate(Profile() with { Equipment = [new(int.MaxValue, (int)InventoryFlag.LowSlot0, 1)] }));
        Assert.NotNull(validator.Validate(Profile() with { Equipment = [new(1317, (int)InventoryFlag.LowSlot0, 2)] }));
        Assert.NotNull(validator.Validate(Profile() with { Equipment = [new(1317, (int)InventoryFlag.HighSlot0, 1)] }));
        Assert.NotNull(validator.Validate(Profile() with { Equipment = [new(1317, (int)InventoryFlag.SubsystemSlot0, 1)] }));
        using WorldDbContext world = new(fixture.Database.Path);
        Assert.Empty(world.Set<SavedTrafficIdentity>());
        Assert.DoesNotContain(world.Pilots, pilot => pilot.Name == "Traffic validation");
    }

    [Fact]
    public void CompleteVictimFitPreservesLoadedAmmunitionAndRejectsExcessAfterCacheWarmup()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        var profile = Profile() with
        {
            HullTypeId = 603,
            Equipment = [new(561, (int)InventoryFlag.HighSlot0, 1), new(215, (int)InventoryFlag.HighSlot0, 50)],
        };
        using (TrafficFitValidator validator = new(fixture.Database.CatalogPath))
        {
            Assert.Null(validator.Validate(profile));
            Assert.Null(
                validator.Validate(
                    profile with
                    {
                        Equipment = [new(561, (int)InventoryFlag.HighSlot0, 1), new(215, (int)InventoryFlag.HighSlot0, 1)],
                    }
                )
            );
            Assert.NotNull(
                validator.Validate(
                    profile with
                    {
                        Equipment = [new(561, (int)InventoryFlag.HighSlot0, 1), new(215, (int)InventoryFlag.HighSlot0, 10000)],
                    }
                )
            );
        }
        Install(fixture.Database, profile);
        new CapsuleerProvisioner(fixture.Database, fixture.Clock).EnsureSystem(Source);
        using WorldDbContext world = new(fixture.Database.Path);
        var identity = Assert.Single(world.Set<SavedTrafficIdentity>());
        long ship = world.Pilots.Single(value => value.Id == identity.PilotId).ActiveShipId;
        var module = Assert.Single(
            world.Set<OwnedItem>().Where(value => value.LocationId == ship && value.FlagId == (int)InventoryFlag.HighSlot0)
        );
        var charge = Assert.Single(world.Set<OwnedItem>().Where(value => value.LocationId == module.Id));
        Assert.Equal(215, charge.TypeId);
        Assert.Equal(50, charge.Quantity);
        Assert.Equal(identity.PilotId, charge.OwnerId);
        Assert.Contains(
            world.Set<OwnedItem>(),
            value => value.LocationId == ship && value.TypeId == 215 && value.FlagId == (int)InventoryFlag.Cargo && value.Quantity > 0
        );
        Assert.Contains(world.Set<SavedCapsuleerSupplies>(), value => value.PilotId == identity.PilotId && value.ShipId == ship);
    }

    [Fact]
    public void PoolUsesOlderCompatibleFitWhenLatestHullCannotTravelTheRoute()
    {
        var older = Profile();
        var newer = older with { KillId = older.KillId + 1, KilledAt = older.KilledAt.AddDays(1), HullTypeId = int.MaxValue };
        TrafficSnapshot snapshot = new(null, [older, newer]);
        Assert.Equal(
            older,
            Assert.Single(
                snapshot.Candidates(42, Source, 0, older.RegionId, older.SecurityBand, profile => profile.HullTypeId != int.MaxValue)
            )
        );
    }

    [Fact]
    public void SelectionPrefersRegionThenBandThenGlobalAndUsesLatestVictim()
    {
        var regional = Profile() with { RegionId = 1, KillId = 1 };
        var old = regional with { KillId = 2, KilledAt = regional.KilledAt.AddDays(-1) };
        var band = Profile(900000002, "Another Traffic Pilot") with { RegionId = 2, KillId = 3 };
        var global = Profile(900000003, "Third Traffic Pilot") with { RegionId = 2, SecurityBand = TrafficSecurityBand.Null, KillId = 4 };
        TrafficSnapshot snapshot = new(null, [old, global, band, regional]);
        var selected = snapshot.Candidates(42, Source, 0, 1, TrafficSecurityBand.High).ToArray();
        Assert.Equal(new[] { regional, band, global }, selected);
        Assert.Equal(selected, snapshot.Candidates(42, Source, 0, 1, TrafficSecurityBand.High));
    }

    [Theory]
    [InlineData(HullClass.Titan, false, false)]
    [InlineData(HullClass.Supercarrier, false, false)]
    [InlineData(HullClass.Carrier, true, false)]
    [InlineData(HullClass.Carrier, false, true)]
    [InlineData(HullClass.Freighter, true, true)]
    [InlineData(HullClass.Frigate, true, true)]
    [InlineData(HullClass.Capsule, false, false)]
    public void RouteAdmissionRejectsUnsupportedTravel(HullClass hull, bool highSecurity, bool expected) =>
        Assert.Equal(expected, TrafficRules.RouteAllows(hull, highSecurity));

    [Theory]
    [InlineData(-1, TrafficSecurityBand.Null)]
    [InlineData(0, TrafficSecurityBand.Null)]
    [InlineData(0.01, TrafficSecurityBand.Low)]
    [InlineData(0.4499, TrafficSecurityBand.Low)]
    [InlineData(0.45, TrafficSecurityBand.High)]
    [InlineData(1, TrafficSecurityBand.High)]
    public void SecurityPoolsUseExistingDisplayedSecurityRules(double security, TrafficSecurityBand expected) =>
        Assert.Equal(expected, TrafficRules.SecurityBand(security));

    [Fact]
    public void ReservedIdentityAndNameCollisionsFallBackWithoutRenamingOrDuplicating()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        var profile = Profile();
        Install(fixture.Database, profile);
        var provisioner = new CapsuleerProvisioner(fixture.Database, fixture.Clock);
        provisioner.EnsureSystem(Source);
        provisioner.EnsureSystem(30000142);
        using WorldDbContext world = new(fixture.Database.Path);
        Assert.Single(world.Set<SavedTrafficIdentity>());
        Assert.Single(world.Pilots.Where(pilot => pilot.Name == profile.Name));
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        Assert.Null(
            TrafficProvisioning.Select(fixture.Database, world, catalog, 1, Source, 3, new(60015157, 60003760, [Source, 30000142]))
        );
    }

    [Fact]
    public void HumanNameCollisionDoesNotReserveOrRenameSourcePilot()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        string name;
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            name = world.Pilots.First().Name;
        }
        Install(fixture.Database, Profile(name: name));
        new CapsuleerProvisioner(fixture.Database, fixture.Clock).EnsureSystem(Source);
        using WorldDbContext saved = new(fixture.Database.Path);
        Assert.Empty(saved.Set<SavedTrafficIdentity>());
        Assert.Single(saved.Pilots.Where(pilot => pilot.Name == name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportedTravelDoesNotRequireTheLegacyStarterOreGrant(bool inbound)
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                UPDATE SourceRecord SET Data='{{}}' WHERE Dataset='client/types' AND Key IN
                    (SELECT CAST(t.Id AS TEXT) FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE g.CategoryId=25)
                """
            );
        }
        Install(fixture.Database, Profile());
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
        {
            Assert.Equal(0, CapsuleerProvisioningCatalog.FreightOre(catalog));
        }
        var provisioner = new CapsuleerProvisioner(fixture.Database, fixture.Clock);
        if (inbound)
        {
            Assert.NotNull(provisioner.EnsureInbound(new(Source, 30000142, 50009991, 50009992)));
        }
        else
        {
            provisioner.EnsureSystem(Source);
        }
        using WorldDbContext saved = new(fixture.Database.Path);
        var identity = Assert.Single(saved.Set<SavedTrafficIdentity>());
        long ship = saved.Pilots.Single(pilot => pilot.Id == identity.PilotId).ActiveShipId;
        Assert.All(
            saved.Set<OwnedItem>().Where(item => item.LocationId == ship),
            item => Assert.NotEqual((int)InventoryFlag.Cargo, item.FlagId)
        );
    }

    [Fact]
    public void CorruptOrMismatchedSnapshotFallsBackAndPreviouslyBoundPilotsRemainSaved()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        Install(fixture.Database, Profile());
        new CapsuleerProvisioner(fixture.Database, fixture.Clock).EnsureSystem(Source);
        string path = Path.Combine(Path.GetDirectoryName(fixture.Database.Path)!, "traffic.sqlite");
        Assert.Throws<InvalidDataException>(() => TrafficSnapshot.Read(path, "wrong catalog"));
        using (SqliteConnection connection = new($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Profile SET Payload=replace(Payload,'Real Traffic Pilot','Changed Traffic Pilot')";
            command.ExecuteNonQuery();
        }
        fixture.Database.LoadTraffic();
        Assert.Empty(fixture.Database.Traffic.Profiles);
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using WorldDbContext world = new(fixture.Database.Path);
        Assert.Single(world.Set<SavedTrafficIdentity>());
        Assert.Single(world.Pilots.Where(pilot => pilot.Name == "Real Traffic Pilot"));
    }

    [Fact]
    public void NewHostWrapperLoadsSnapshotOnceAndKeepsItUntilRestart()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        Install(fixture.Database, Profile());
        using var host = ServerServices.Build(new WorldDatabase(fixture.Database.Path), fixture.Clock);
        var database = host.GetRequiredService<WorldDatabase>();
        Assert.Equal("Real Traffic Pilot", Assert.Single(database.Traffic.Profiles).Name);
        Install(fixture.Database, Profile(name: "Updated Traffic Pilot"));
        Assert.Equal("Real Traffic Pilot", Assert.Single(database.Traffic.Profiles).Name);
        using var restarted = ServerServices.Build(new WorldDatabase(fixture.Database.Path), fixture.Clock);
        Assert.Equal("Updated Traffic Pilot", Assert.Single(restarted.GetRequiredService<WorldDatabase>().Traffic.Profiles).Name);
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("pilot", false)]
    [InlineData("ship", false)]
    [InlineData("equipment", false)]
    [InlineData("damage", false)]
    [InlineData("cargo", false)]
    [InlineData("flight", false)]
    public void DockedRefreshPreservesIdentityAssetsAndCustomizedOrIneligibleShips(string changed, bool expected)
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        new CapsuleerProvisioner(fixture.Database, fixture.Clock).EnsureSystem(Source);
        long pilotId;
        long shipId;
        using (WorldDbContext setup = new(fixture.Database.Path))
        {
            var agent = setup.Set<SavedCapsuleerAgent>().Single(value => value.Career == CapsuleerCareer.Hauling);
            pilotId = agent.PilotId;
            shipId = agent.FounderShipId;
            setup.Database.ExecuteSql($"DELETE FROM WorldPosition WHERE ShipId={shipId}");
            if (changed == "flight")
            {
                setup.Add(new Eden.Data.World.Docking.FlightPosition { ShipId = shipId, SystemId = Source });
            }
            if (changed == "pilot")
            {
                setup.Pilots.Single(value => value.Id == pilotId).Name = "Custom Traffic Pilot";
            }
            if (changed == "ship")
            {
                setup.Ships.Single(value => value.Id == shipId).Name = "Custom Traffic Ship";
            }
            if (changed == "equipment")
            {
                setup.Add(
                    new OwnedItem
                    {
                        Id = 300000000001,
                        OwnerId = pilotId,
                        TypeId = 1317,
                        LocationId = shipId,
                        FlagId = (int)InventoryFlag.LowSlot0,
                        Quantity = 1,
                        Singleton = true,
                    }
                );
            }
            if (changed == "damage")
            {
                setup.Set<ShipHealthState>().RemoveRange(setup.Set<ShipHealthState>().Where(value => value.ShipId == shipId));
                setup.Add(new ShipHealthState { ShipId = shipId, ArmorDamage = 1 });
            }
            if (changed == "cargo")
            {
                setup
                    .Set<OwnedItem>()
                    .Where(value => value.LocationId == shipId)
                    .ExecuteUpdate(value => value.SetProperty(item => item.Quantity, 10000000));
            }
            setup.SaveChanges();
        }
        Install(fixture.Database, Profile() with { HullTypeId = 603 });
        using WorldDbContext world = new(fixture.Database.Path);
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        var cargoBefore = world
            .Set<OwnedItem>()
            .Where(value => value.LocationId == shipId && value.FlagId == (int)InventoryFlag.Cargo)
            .Select(value => new
            {
                value.Id,
                value.TypeId,
                value.Quantity,
                value.OwnerId,
            })
            .ToArray();
        var jobsBefore = CapsuleerPopulationStore.Jobs(world);
        using (var transaction = world.Database.BeginTransaction())
        {
            Assert.Equal(
                expected,
                TrafficProvisioning.Refresh(
                    fixture.Database,
                    world,
                    catalog,
                    pilotId,
                    new(60015157, 60003760, [Source, 30000142]),
                    fixture.Clock.Now
                )
            );
            transaction.Commit();
        }
        Assert.Equal(
            cargoBefore,
            world
                .Set<OwnedItem>()
                .Where(value => value.LocationId == shipId && value.FlagId == (int)InventoryFlag.Cargo)
                .Select(value => new
                {
                    value.Id,
                    value.TypeId,
                    value.Quantity,
                    value.OwnerId,
                })
                .ToArray()
        );
        Assert.Equal(shipId, world.Pilots.Single(value => value.Id == pilotId).ActiveShipId);
        Assert.Equal(jobsBefore, CapsuleerPopulationStore.Jobs(world));
        Assert.Equal(expected ? 1 : 0, world.Set<SavedTrafficIdentity>().Count());
        Assert.Equal(expected ? 603 : 648, world.Ships.Single(value => value.Id == shipId).TypeId);
    }

    [Fact]
    public void FailedDockedRefreshLeavesHullNameSkillsCargoAndReservationUnchanged()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        new CapsuleerProvisioner(fixture.Database, fixture.Clock).EnsureSystem(Source);
        long pilot;
        long ship;
        string originalName;
        using (WorldDbContext setup = new(fixture.Database.Path))
        {
            var agent = setup.Set<SavedCapsuleerAgent>().Single(value => value.Career == CapsuleerCareer.Hauling);
            pilot = agent.PilotId;
            ship = agent.FounderShipId;
            originalName = setup.Pilots.Single(value => value.Id == pilot).Name;
            setup.Database.ExecuteSql($"DELETE FROM WorldPosition WHERE ShipId={ship}");
            setup.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER reject_refresh BEFORE INSERT ON WorldTrafficIdentity
                BEGIN SELECT RAISE(ABORT,'traffic refresh rollback'); END;
                """
            );
        }
        Install(fixture.Database, Profile() with { HullTypeId = 603 });
        Assert.Throws<DbUpdateException>(() =>
        {
            using WorldDbContext world = new(fixture.Database.Path);
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using var transaction = world.Database.BeginTransaction();
            TrafficProvisioning.Refresh(
                fixture.Database,
                world,
                catalog,
                pilot,
                new(60015157, 60003760, [Source, 30000142]),
                fixture.Clock.Now
            );
            transaction.Commit();
        });
        using WorldDbContext saved = new(fixture.Database.Path);
        Assert.Equal(648, saved.Ships.Single(value => value.Id == ship).TypeId);
        Assert.Equal(originalName, saved.Pilots.Single(value => value.Id == pilot).Name);
        Assert.Empty(saved.Set<SavedTrafficIdentity>());
        var cargo = Assert.Single(saved.Set<OwnedItem>().Where(value => value.LocationId == ship));
        Assert.Equal((int)InventoryFlag.Cargo, cargo.FlagId);
        Assert.Equal(100, cargo.Quantity);
    }

    [Fact]
    public void FailedProvisioningRollsBackIdentityEquipmentAndReservation()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        Install(fixture.Database, Profile());
        long[] before;
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            before = world.Pilots.Select(value => value.Id).ToArray();
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER reject_traffic BEFORE INSERT ON WorldTrafficIdentity
                BEGIN SELECT RAISE(ABORT,'traffic rollback'); END;
                """
            );
        }
        Assert.Throws<DbUpdateException>(() => new CapsuleerProvisioner(fixture.Database, fixture.Clock).EnsureSystem(Source));
        using WorldDbContext saved = new(fixture.Database.Path);
        Assert.Equal(before, saved.Pilots.Select(value => value.Id));
        Assert.Empty(saved.Set<SavedTrafficIdentity>());
        Assert.Empty(saved.Set<SavedCapsuleerAgent>());
    }

    internal static TrafficProfile Profile(long character = 900000001, string name = "Real Traffic Pilot") =>
        new(
            character,
            name,
            123456789,
            DateTimeOffset.Parse("2026-11-01T12:00:00Z"),
            Source,
            10000002,
            TrafficSecurityBand.High,
            648,
            [new(1317, (int)InventoryFlag.LowSlot0, 1)]
        );

    internal static void Install(WorldDatabase database, params TrafficProfile[] profiles)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        string fingerprint = TrafficSnapshot.Fingerprint(catalog);
        var metadata = new TrafficSnapshotManifest(
            TrafficRules.SnapshotVersion,
            TrafficSnapshot.Identity(fingerprint, profiles),
            fingerprint,
            "2026-11-01",
            "2026-11-01",
            [],
            profiles.Length,
            0,
            profiles.Length,
            profiles.Select(value => value.CharacterId).Distinct().Count(),
            profiles.Select(value => value.HullTypeId).Distinct().Count(),
            profiles.Select(TrafficSnapshot.Signature).Distinct().Count(),
            []
        );
        string path = Path.Combine(Path.GetDirectoryName(database.Path)!, "traffic.sqlite");
        File.Delete(path);
        TrafficSnapshot.Write(path, metadata, profiles);
        database.LoadTraffic();
    }
}
