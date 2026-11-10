using Eden.Data.Catalog;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerProvisioningTests(ITestOutputHelper output)
{
    private const int Source = 30000141;
    private const int Destination = 30000142;
    private const int Third = 30000143;
    private const int DestinationStation = 60003760;

    [Fact]
    public void LocalPreparationDoesNotReadUnrelatedSystemPayloads()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord VALUES ('client/solarsystemcontent','30000144','invalid-unrelated-json')
                """
            );
        }
        Assert.Equal(1, new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Source).CreatedCommunities);
    }

    [Fact]
    public void BusyCorridorReceivesTrafficWhileQuietMiningAreaCanHaveOnlyAMiner()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        using (WorldDbContext world = new(f.Database.Path))
        {
            world.Set<SavedCapsuleerProvisioning>().Single().WorldSeed = 2;
            world.SaveChanges();
        }
        var provisioner = new CapsuleerProvisioner(f.Database, f.Clock);
        provisioner.EnsureSystem(Source);
        using (WorldDbContext world = new(f.Database.Path))
        {
            Assert.Equal(CapsuleerCareer.Mining, Assert.Single(world.Set<SavedCapsuleerAgent>()).Career);
        }
        AddSystem(f.Database, 31000001);
        AddSystem(f.Database, 31000002);
        provisioner.EnsureSystem(Source);
        using WorldDbContext saved = new(f.Database.Path);
        int haulers = CapsuleerEncounterRules.Haulers(2, Source, 3, 1);
        Assert.Equal(1 + haulers, saved.Set<SavedCapsuleerAgent>().Count());
        var pilots = saved.Set<SavedCapsuleerAgent>().Where(a => a.Career == CapsuleerCareer.Hauling).OrderBy(a => a.Slot).ToArray();
        Assert.Equal(Enumerable.Range(0, haulers), pilots.Select(a => a.Slot));
        Assert.Equal(0, provisioner.EnsureSystem(Source).CreatedCommunities);
        Assert.Equal(haulers, saved.Set<SavedCapsuleerHauler>().Count());
    }

    [Fact]
    public void InterruptedHubUpgradeRetainsTheExistingPilotAndRetriesOnlyMissingSlots()
    {
        using CapsuleerHaulerTests.Fixture f = new(isolateRoute: true);
        for (int i = 1; i <= 5; i++)
        {
            AddSystem(f.Database, 31000000 + i);
        }
        long founder;
        long originalShip;
        long originalBalance;
        CapsuleerJob[] originalJobs;
        using (WorldDbContext world = new(f.Database.Path))
        {
            var hauler = world.Set<SavedCapsuleerHauler>().Single();
            founder = hauler.PilotId;
            originalShip = hauler.FounderShipId;
            originalBalance = world.Set<PilotWallet>().Single(w => w.PilotId == founder).Cents;
            originalJobs = CapsuleerPopulationStore.Jobs(world);
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER interrupt_hub_upgrade BEFORE INSERT ON WorldCapsuleerAgent
                WHEN NEW.Slot=2 BEGIN SELECT RAISE(ABORT,'interrupted hub expansion'); END
                """
            );
        }
        var provisioner = new CapsuleerProvisioner(f.Database, f.Clock);
        Assert.Throws<SqliteException>(() => provisioner.EnsureSystem(Source));
        using (WorldDbContext world = new(f.Database.Path))
        {
            Assert.Equal(originalJobs, CapsuleerPopulationStore.Jobs(world));
            Assert.Single(world.Set<SavedCapsuleerHauler>());
            Assert.Equal(2, world.Set<SavedCapsuleerAgent>().Count());
            world.Database.ExecuteSqlRaw("DROP TRIGGER interrupt_hub_upgrade");
        }
        Assert.Equal(1, provisioner.EnsureSystem(Source).CreatedCommunities);
        Assert.Equal(0, provisioner.EnsureSystem(Source).CreatedCommunities);
        WorldInitialization.Initialize(f.Database, f.Clock);
        using WorldDbContext saved = new(f.Database.Path);
        Assert.InRange(saved.Set<SavedCapsuleerHauler>().Count(), 5, 6);
        Assert.Equal(originalShip, saved.Pilots.Single(p => p.Id == founder).ActiveShipId);
        Assert.Equal(originalBalance, saved.Set<PilotWallet>().Single(w => w.PilotId == founder).Cents);
        Assert.Equal(originalJobs, CapsuleerPopulationStore.Jobs(saved).Where(j => originalJobs.Any(old => old.PilotId == j.PilotId)));
    }

    [Fact]
    public void MarketWithNoBeltsCanReceiveHaulingWithoutCreatingAMinerOrAsteroids()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        using (WorldDbContext world = new(f.Database.Path))
        {
            world.Set<SavedCapsuleerProvisioning>().Single().WorldSeed = Destination;
            world.SaveChanges();
        }
        new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Destination);
        using WorldDbContext saved = new(f.Database.Path);
        Assert.Equal(CapsuleerCareer.Hauling, Assert.Single(saved.Set<SavedCapsuleerAgent>()).Career);
        Assert.Empty(saved.Set<WorldAsteroid>().Where(a => a.SystemId == Destination));
    }

    [Fact]
    public void UnchangedProvisioningUsesSavedEligibilityWithBoundedDatabaseWork()
    {
        using CapsuleerHaulerTests.Fixture f = new(isolateRoute: true);
        var provisioner = new CapsuleerProvisioner(f.Database, f.Clock);
        var first = provisioner.EnsureSystem(Source);
        using var metrics = SessionPerformance.Begin("unchanged eligibility", output.WriteLine, force: true)!;
        var second = provisioner.EnsureSystem(Source);
        Assert.Equal(first.RegisteredSystems, second.RegisteredSystems);
        Assert.Equal(0, second.CreatedCommunities);
        Assert.InRange(metrics.Commands, 1, 12);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void RenewedDepositsRetryDeferredProvisioningWithoutCatalogChanges()
    {
        using CapsuleerHaulerTests.Fixture f = new(isolateRoute: true);
        AddDestinationOre(f.Database);
        using (WorldDbContext world = new(f.Database.Path))
        using (CatalogDbContext catalog = new(f.Database.CatalogPath))
        {
            Eden.World.Mining.AsteroidFieldStore.Populate(world, catalog, Destination, f.Clock.Now);
            world.SaveChanges();
            world.Set<WorldAsteroid>().Where(a => a.SystemId == Destination).ExecuteUpdate(s => s.SetProperty(a => a.Quantity, 0));
        }
        var provisioner = new CapsuleerProvisioner(f.Database, f.Clock);
        Assert.Equal(1, provisioner.EnsureSystem(Destination).CreatedCommunities);
        using (WorldDbContext world = new(f.Database.Path))
        {
            world.Set<WorldAsteroid>().Where(a => a.SystemId == Destination).ExecuteUpdate(s => s.SetProperty(a => a.Quantity, 100));
        }
        Assert.Equal(1, provisioner.EnsureSystem(Destination).CreatedCommunities);
        Assert.Equal(0, provisioner.EnsureSystem(Destination).CreatedCommunities);
    }

    [Fact]
    public void EligibleSystemsReceiveOneAtomicPairWithStableNamesSkillsAndStaggeredJobs()
    {
        using CapsuleerHaulerTests.Fixture f = new(isolateRoute: true);
        CapsuleerJob[] established;
        long cents;
        using (WorldDbContext world = new(f.Database.Path))
        {
            established = CapsuleerPopulationStore.Jobs(world);
            cents = world.Set<PilotWallet>().Sum(w => w.Cents);
        }
        AddDestinationOre(f.Database);
        var turn = new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Destination);
        output.WriteLine($"Registered {turn.RegisteredSystems} systems; created {turn.CreatedCommunities} community.");
        Assert.Equal(1, turn.CreatedCommunities);
        Assert.Equal(0, turn.PendingCommunities);
        using WorldDbContext saved = new(f.Database.Path);
        Assert.Equal(2, saved.Set<SavedCapsuleerPopulation>().Count());
        Assert.Equal(4, saved.Set<SavedCapsuleerAgent>().Count());
        Assert.Equal(
            cents + CapsuleerCommunityStore.StartingCents + HaulingRules.StartingCents,
            saved.Set<PilotWallet>().Sum(w => w.Cents)
        );
        Assert.Equal(established, CapsuleerPopulationStore.Jobs(saved).Where(j => established.Any(old => old.PilotId == j.PilotId)));
        long seed = saved.Set<SavedCapsuleerProvisioning>().Single().WorldSeed;
        foreach (var agent in saved.Set<SavedCapsuleerAgent>().Where(a => a.CommunitySystemId == Destination).ToArray())
        {
            var pilot = saved.Pilots.Single(p => p.Id == agent.PilotId);
            Assert.Equal(CapsuleerProvisioningRules.Name(seed, Destination, agent.Career), pilot.Name);
            Assert.Equal(CharacterNameResult.Available, CharacterRules.ValidateName(pilot.Name, false));
            Assert.Equal(CapsuleerCommunityStore.MinerAccountId, pilot.AccountId);
            Assert.Equal(pilot.ActiveShipId, agent.FounderShipId);
            var ship = saved.Ships.Single(s => s.Id == pilot.ActiveShipId);
            Assert.Equal(
                agent.Career == CapsuleerCareer.Mining ? CapsuleerCommunityStore.VentureType : HaulingRules.BadgerType,
                ship.TypeId
            );
            Assert.Equal(DestinationStation, ship.StationId);
            var job = saved.Set<SavedCapsuleerJob>().Single(j => j.PilotId == pilot.Id);
            long due =
                job.Action == CapsuleerAction.Arrival
                    ? TimeSpan.TicksPerSecond
                    : CapsuleerProvisioningRules.FirstDueTicks(0, seed, Destination, agent.Career);
            Assert.Equal(due, job.DueTicks);
            Assert.InRange(job.DueTicks, TimeSpan.TicksPerMillisecond, TimeSpan.FromSeconds(30).Ticks);
            Assert.Equal(0, job.Revision);
            using CatalogDbContext catalog = new(f.Database.CatalogPath);
            Assert.Equal(pilot.RaceId, catalog.Read(CatalogData.Client.Bloodlines, (long)pilot.BloodlineId)!.RaceID);
            Assert.Equal(pilot.RaceId, catalog.Read(CatalogData.Client.Schools, (long)pilot.SchoolId)!.RaceID);
            Assert.NotEmpty(saved.Set<Eden.Data.World.Skills.PilotSkill>().Where(s => s.PilotId == pilot.Id));
        }
        Assert.Equal(2, saved.Set<SavedCapsuleerCommunity>().Count());
        Assert.Equal(2, saved.Set<SavedCapsuleerHauler>().Count());
        Assert.Equal(Destination, new CapsuleerPopulationStore(f.Database).Systems().Single(s => s.SystemId == Destination).SystemId);
        Assert.Equal(0, new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Destination).CreatedCommunities);
        WorldInitialization.Initialize(f.Database, f.Clock);
        using WorldDbContext reopened = new(f.Database.Path);
        Assert.Equal(4, reopened.Set<SavedCapsuleerAgent>().Count());
        Assert.Equal(seed, reopened.Set<SavedCapsuleerProvisioning>().Single().WorldSeed);
        Assert.Equal(
            cents + CapsuleerCommunityStore.StartingCents + HaulingRules.StartingCents,
            reopened.Set<PilotWallet>().Sum(w => w.Cents)
        );
    }

    [Fact]
    public void DepletedSavedFieldsDeferOnlyMiningAndPreserveHauling()
    {
        using CapsuleerHaulerTests.Fixture f = new(isolateRoute: true);
        AddDestinationOre(f.Database);
        using (WorldDbContext world = new(f.Database.Path))
        using (CatalogDbContext catalog = new(f.Database.CatalogPath))
        {
            Eden.World.Mining.AsteroidFieldStore.Populate(world, catalog, Destination, f.Clock.Now);
            world.SaveChanges();
            foreach (var asteroid in world.Set<WorldAsteroid>().Where(a => a.SystemId == Destination))
            {
                asteroid.Quantity = 0;
            }
            world.SaveChanges();
        }
        Assert.Equal(1, new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Destination).CreatedCommunities);
        Assert.Equal(
            CapsuleerEligibility.Eligible,
            new CapsuleerPopulationStore(f.Database).Systems().Single(s => s.SystemId == Destination).Eligibility
        );
        using WorldDbContext saved = new(f.Database.Path);
        Assert.All(saved.Set<WorldAsteroid>().Where(a => a.SystemId == Destination), a => Assert.Equal(0, a.Quantity));
        var agent = Assert.Single(saved.Set<SavedCapsuleerAgent>().Where(a => a.CommunitySystemId == Destination));
        Assert.Equal(CapsuleerCareer.Hauling, agent.Career);
    }

    [Fact]
    public void AFailedSecondFounderRollsBackTheEntireCommunityAndRetryGrantsOnce()
    {
        using CapsuleerHaulerTests.Fixture f = new(isolateRoute: true);
        AddDestinationOre(f.Database);
        long balance;
        int pilots;
        int ships;
        using (WorldDbContext world = new(f.Database.Path))
        {
            balance = world.Set<PilotWallet>().Sum(w => w.Cents);
            pilots = world.Pilots.Count();
            ships = world.Ships.Count();
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER reject_global_hauler BEFORE INSERT ON WorldCapsuleerHauler
                WHEN NEW.Id=30000142 BEGIN SELECT RAISE(ABORT,'provisioning interruption'); END
                """
            );
        }
        Assert.IsType<SqliteException>(
            Assert.Throws<DbUpdateException>(() => new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Destination)).InnerException
        );
        using (WorldDbContext world = new(f.Database.Path))
        {
            Assert.Equal(balance, world.Set<PilotWallet>().Sum(w => w.Cents));
            Assert.Equal(pilots, world.Pilots.Count());
            Assert.Equal(ships, world.Ships.Count());
            Assert.DoesNotContain(world.Set<SavedCapsuleerPopulation>(), p => p.SystemId == Destination);
            Assert.DoesNotContain(world.Set<SavedCapsuleerAgent>(), a => a.CommunitySystemId == Destination);
            Assert.DoesNotContain(world.Set<SavedCapsuleerCommunity>(), c => c.Id == Destination);
            world.Database.ExecuteSqlRaw("DROP TRIGGER reject_global_hauler");
        }
        Assert.Equal(1, new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Destination).CreatedCommunities);
        Assert.Equal(0, new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Destination).CreatedCommunities);
    }

    [Fact]
    public void BoundedProvisioningResumesAcrossReopenWithoutReplacingCompletedCommunities()
    {
        using CapsuleerHaulerTests.Fixture f = new(isolateRoute: true);
        AddDestinationOre(f.Database);
        AddSystem(f.Database, Third);
        var first = new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Destination);
        Assert.Equal(1, first.CreatedCommunities);
        Assert.Equal(0, first.PendingCommunities);
        string[] names;
        long seed;
        using (WorldDbContext world = new(f.Database.Path))
        {
            names = world.Pilots.Where(p => p.AccountId < 0).OrderBy(p => p.Id).Select(p => p.Name).ToArray();
            seed = world.Set<SavedCapsuleerProvisioning>().Single().WorldSeed;
        }
        var resumed = new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Third);
        Assert.Equal(1, resumed.CreatedCommunities);
        Assert.Equal(0, resumed.PendingCommunities);
        using WorldDbContext reopened = new(f.Database.Path);
        Assert.Equal(5, reopened.Set<SavedCapsuleerAgent>().Count());
        Assert.Equal(names, reopened.Pilots.Where(p => p.AccountId < 0).OrderBy(p => p.Id).Take(names.Length).Select(p => p.Name));
        Assert.Equal(seed, reopened.Set<SavedCapsuleerProvisioning>().Single().WorldSeed);
        Assert.Equal(0, new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Destination).CreatedCommunities);
    }

    [Fact]
    public void ConfigurationDefersNewGrantsAndRetainsEstablishedPossessionsAndDeadlines()
    {
        using CapsuleerHaulerTests.Fixture f = new(isolateRoute: true);
        AddDestinationOre(f.Database);
        var configuration = new WorldConfigurationStore(f.Database, f.Clock);
        var state = configuration.Read();
        configuration.Save(
            state.Generation,
            state.Active.Draft with
            {
                Settings = state.Active.Draft.Settings with { Capsuleers = new(false) },
            },
            "test"
        );
        WorldInitialization.Initialize(f.Database, f.Clock);
        var population = new CapsuleerPopulationStore(f.Database);
        Assert.Equal(2, population.Summary().Agents);
        Assert.Empty(population.Systems());
        state = configuration.Read();
        configuration.Save(
            state.Generation,
            state.Active.Draft with
            {
                Settings = state.Active.Draft.Settings with { Capsuleers = new(true) },
            },
            "test"
        );
        WorldInitialization.Initialize(f.Database, f.Clock);
        new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Destination);
        Assert.Equal(4, population.Summary().Agents);
    }

    [Fact]
    public void MultipleCommunitiesRunTheRealQueueWithoutCreatingBackgroundNativeScenes()
    {
        using CapsuleerHaulerTests.Fixture f = new(isolateRoute: true);
        AddDestinationOre(f.Database);
        new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Destination);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        var presence = host.GetRequiredService<IHumanPresence>();
        Assert.Equal(4, scheduler.Jobs().Count);
        Assert.All(host.GetRequiredService<ICapsuleerHandoff>().Systems(), s => Assert.Equal(SystemSimulationMode.Background, s.Mode));
        using var selected = presence.Select(90000001, Third);
        for (int i = 0; i < 35; i++)
        {
            f.Clock.Seconds++;
            host.GetRequiredService<CapsuleerMaintenance>().Tick();
        }
        Assert.All(scheduler.Jobs(), j => Assert.Equal(0, j.Sequence));
        Assert.Equal(0, host.GetRequiredService<CapsuleerScene>().MaterializedPilots);
        foreach (var source in new[] { Source, Destination })
        {
            var agents = host.GetRequiredService<ICapsuleerPopulation>().Agents(source);
            long miner = agents.Single(a => a.Career == CapsuleerCareer.Mining).PilotId;
            Assert.Equal(source, scheduler.Miner(miner)!.Pilot.SystemId);
        }
        Assert.Throws<InvalidOperationException>(() => host.GetRequiredService<ICapsuleerProvisioning>().Reconcile());
    }

    [Theory]
    [InlineData(0.449, CapsuleerEligibility.UnsupportedSecurity)]
    [InlineData(0.45, CapsuleerEligibility.Eligible)]
    [InlineData(double.NaN, CapsuleerEligibility.UnsupportedSecurity)]
    [InlineData(double.PositiveInfinity, CapsuleerEligibility.UnsupportedSecurity)]
    public void SecurityBoundaryUsesTheCatalogHighSecurityRoundingRule(double security, CapsuleerEligibility expected) =>
        Assert.Equal(expected, CapsuleerProvisioningRules.Eligibility(new(true, security, true, true, true, true, true, true)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HaulingAcceptsOrelessSystemsAndRejectsBrokenRoutes(bool missingOre)
    {
        using CapsuleerHaulerTests.Fixture f = new(isolateRoute: true);
        AddDestinationOre(f.Database);
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                missingOre
                    ? "UPDATE SourceRecord SET Data='{{}}' WHERE Dataset='client/asteroidcountbytypeidbeltid' AND Key='40009052'"
                    : "UPDATE Stargate SET DestinationId=50009992 WHERE Id=50009992"
            );
        }
        new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Destination);
        var registry = new CapsuleerPopulationStore(f.Database).Systems();
        Assert.Equal(
            missingOre ? CapsuleerEligibility.Eligible : CapsuleerEligibility.NoReciprocalRoute,
            registry.Single(s => s.SystemId == Destination).Eligibility
        );
        using WorldDbContext world = new(f.Database.Path);
        Assert.Equal(missingOre ? 3 : 2, world.Set<SavedCapsuleerAgent>().Count());
        using CatalogDbContext source = new(f.Database.CatalogPath);
        Assert.Single(registry);
    }

    internal static void AddDestinationOre(WorldDatabase database)
    {
        using (WorldDbContext world = new(database.Path))
        {
            world.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO WorldCapsuleerProvisioning(Id,WorldSeed) VALUES(1,30000142)");
        }
        using CatalogDbContext catalog = new(database.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z)
                VALUES(40009052,30000142,15,9,-37717647360,849715200,-55091896320);
            INSERT INTO SourceRecord VALUES('client/asteroidcountbytypeidbeltid','40009052','{{"1230":39}}');
            """
        );
    }

    internal static void AddSystem(WorldDatabase database, int system)
    {
        using CatalogDbContext catalog = new(database.CatalogPath, writable: true);
        using var transaction = catalog.Database.BeginTransaction();
        catalog.Database.ExecuteSqlRaw("PRAGMA defer_foreign_keys=ON");
        const int FirstStation = 62000000;
        const int FirstBelt = 44000000;
        const int FirstGate = 54000000;
        int ordinal = system - 30000000;
        int station = FirstStation + ordinal;
        int belt = FirstBelt + ordinal;
        int gate = FirstGate + ordinal * 2;
        var reference = catalog.Set<Station>().AsNoTracking().Single(s => s.Id == DestinationStation);
        catalog.Add(
            new SolarSystem
            {
                Id = system,
                Name = "Provisioned " + system,
                ConstellationId = 20000020,
            }
        );
        catalog.Add(
            new Station
            {
                Id = station,
                SolarSystemId = system,
                TypeId = reference.TypeId,
                OwnerId = reference.OwnerId,
                OperationId = reference.OperationId,
                OrbitId = reference.OrbitId,
                X = reference.X,
                Y = reference.Y,
                Z = reference.Z,
            }
        );
        catalog.Add(
            new Celestial
            {
                Id = belt,
                SolarSystemId = system,
                TypeId = 15,
                GroupId = 9,
            }
        );
        catalog.Add(
            new Celestial
            {
                Id = gate,
                SolarSystemId = system,
                TypeId = 16,
                GroupId = 10,
                Radius = 3500,
            }
        );
        catalog.Add(
            new Celestial
            {
                Id = gate + 1,
                SolarSystemId = Source,
                TypeId = 16,
                GroupId = 10,
                Radius = 3500,
            }
        );
        catalog.SaveChanges();
        catalog.Database.ExecuteSql(
            $"""
            INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES
                ({gate},{system},{gate + 1}),({gate + 1},{Source},{gate})
            """
        );
        catalog.Add(
            new SourceRecord
            {
                Dataset = "client/solarsystemcontent",
                Key = system.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Data = "{\"security\":1.0}",
            }
        );
        catalog.Add(
            new SourceRecord
            {
                Dataset = "client/asteroidcountbytypeidbeltid",
                Key = belt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Data = "{\"1230\":39}",
            }
        );
        catalog.SaveChanges();
        transaction.Commit();
    }
}
