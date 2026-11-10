using System.Text.Json;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Configuration;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Configuration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerGlobalPopulationTests
{
    private const int Source = 30000141;
    private const int Third = 31000001;

    [Fact]
    public void StartupProgressCountsCommittedSystemsAndResumesFromCachedSystems()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        CapsuleerProvisioningTests.AddSystem(f.Database, Third);
        SetPolicy(f, new(Miners: false, TrafficMultiplier: 2, GateVisitors: 0));
        WorldInitialization.Initialize(f.Database, f.Clock);
        List<CapsuleerPreparationProgress> updates = [];
        var partial = new CapsuleerProvisioner(f.Database, f.Clock).Reconcile(maximumCommunities: 1, progress: Record);
        Assert.Equal(0, updates[0].CompletedSystems);
        Assert.Equal(1, updates[^1].CompletedSystems);
        Assert.True(partial.PendingCommunities > 0);

        updates.Clear();
        var finished = WorldInitialization.PrepareCapsuleers(new(f.Database.Path), f.Clock, progress: Record);
        Assert.Equal(1, updates[0].CompletedSystems);
        Assert.Equal(finished.RegisteredSystems, updates[^1].CompletedSystems);
        Assert.Equal(finished.PendingCommunities, updates[^1].DeferredSystems);
        Assert.Equal(0, finished.PendingCommunities);

        updates.Clear();
        WorldInitialization.PrepareCapsuleers(new(f.Database.Path), f.Clock, progress: Record);
        Assert.Equal(new(finished.RegisteredSystems, finished.RegisteredSystems, 0), Assert.Single(updates));

        void Record(CapsuleerPreparationProgress update)
        {
            using WorldDbContext world = new(f.Database.Path);
            // A separate reader must see every system counted as completed.
            Assert.Equal(world.Set<SavedCapsuleerSystem>().Count(), update.CompletedSystems);
            Assert.InRange(update.CompletedSystems, 0, update.TotalSystems);
            if (updates.Count > 0)
            {
                Assert.Equal(updates[^1].CompletedSystems + 1, update.CompletedSystems);
            }
            updates.Add(update);
        }
    }

    [Fact]
    public void GlobalRestartFillsUnvisitedSystemRostersWithoutObservers()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        CapsuleerProvisioningTests.AddSystem(f.Database, Third);
        SetPolicy(f, new(Miners: false, TrafficMultiplier: 10, GateVisitors: 0));
        WorldInitialization.Initialize(f.Database, f.Clock);
        WorldInitialization.PrepareCapsuleers(f.Database, f.Clock);
        using WorldDbContext world = new(f.Database.Path);
        using CatalogDbContext catalog = new(f.Database.CatalogPath);
        foreach (int system in new[] { Source, Third })
        {
            var plan = CapsuleerTrafficRoutes.Read(catalog, system);
            int expected = CapsuleerEncounterRules.Haulers(1, system, plan.GateCount, plan.StationCount, world.Settings.Capsuleers);
            Assert.True(expected > 0);
            Assert.Equal(expected, world.Set<SavedCapsuleerAgent>().Count(a => a.CommunitySystemId == system));
        }
        Assert.Equal(catalog.Definitions.Systems.Count, world.Set<SavedCapsuleerSystem>().Count());
        Assert.All(world.Set<SavedCapsuleerSystem>(), system => Assert.Equal(CapsuleerProvisioningRules.Version, system.PolicyVersion));
        Assert.Equal(0, world.Set<SavedCapsuleerClock>().Single().ElapsedTicks);
    }

    [Fact]
    public void GlobalReductionAndIncreaseReusePossessionsAndKeepPermanentLosses()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        CapsuleerProvisioningTests.AddSystem(f.Database, Third);
        CapsuleerPopulationPolicy policy = new(Miners: false, TrafficMultiplier: 2, GateVisitors: 0);
        Restart(f, policy);
        long[] pilots;
        long lost;
        long capsule;
        using (WorldDbContext world = new(f.Database.Path))
        using (CatalogDbContext catalog = new(f.Database.CatalogPath))
        using (var transaction = world.Database.BeginTransaction())
        {
            pilots = world.Set<SavedCapsuleerAgent>().OrderBy(a => a.PilotId).Select(a => a.PilotId).ToArray();
            var founder = world.Set<SavedCapsuleerAgent>().Single(a => a.CommunitySystemId == Source && a.Slot == 0);
            var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(ship => ship.Position).Single(p => p.Id == founder.PilotId);
            lost = pilot.ActiveShipId;
            pilot.ActiveShip.Position ??= new FlightPosition { ShipId = lost, SystemId = Source };
            const long attacker = 700000002;
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
            ShipLossPersistence.Apply(
                world,
                catalog,
                pilot,
                attacker,
                lost,
                Source,
                pilot.ActiveShip.Position,
                f.Clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
                new NoLoot()
            );
            world.SaveChanges();
            capsule = pilot.ActiveShipId;
            transaction.Commit();
        }
        string assets = Assets(f, pilots);
        Restart(f, policy with { TrafficMultiplier = 0 });
        using (WorldDbContext world = new(f.Database.Path))
        {
            Assert.Equal(pilots.Length, world.Set<SavedCapsuleerSuspension>().Count());
            world.Set<SavedCapsuleerClock>().Single().ElapsedTicks = TimeSpan.FromMinutes(5).Ticks;
            world.SaveChanges();
        }
        Restart(f, policy with { TrafficMultiplier = 0 });
        Assert.Equal(assets, Assets(f, pilots));
        Restart(f, policy);
        using (WorldDbContext world = new(f.Database.Path))
        {
            Assert.Empty(world.Set<SavedCapsuleerSuspension>());
            Assert.Equal(pilots, world.Set<SavedCapsuleerAgent>().OrderBy(a => a.PilotId).Select(a => a.PilotId));
            Assert.NotNull(world.Set<ShipLoss>().Find(lost));
            Assert.Null(world.Ships.Find(lost));
            Assert.Contains(world.Pilots, p => p.ActiveShipId == capsule);
        }
        Assert.Equal(assets, Assets(f, pilots));
        Restart(f, policy with { TrafficMultiplier = 4 });
        using (WorldDbContext world = new(f.Database.Path))
        {
            Assert.True(world.Set<SavedCapsuleerAgent>().Count() > pilots.Length);
        }
        Assert.Equal(assets, Assets(f, pilots));
    }

    [Fact]
    public void InterruptedGlobalStartupCommitsCompletedCommunitiesAndRetriesMissingSlotsOnce()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        CapsuleerProvisioningTests.AddSystem(f.Database, Third);
        SetPolicy(f, new(Miners: false, TrafficMultiplier: 2, GateVisitors: 0));
        WorldInitialization.Initialize(f.Database, f.Clock);
        using (WorldDbContext world = new(f.Database.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER reject_global_expansion BEFORE INSERT ON WorldCapsuleerAgent
                WHEN NEW.CommunitySystemId=31000001 AND NEW.Slot=1
                BEGIN SELECT RAISE(ABORT,'global expansion interrupted'); END;
                """
            );
        }
        List<CapsuleerPreparationProgress> updates = [];
        var error = Assert.ThrowsAny<Exception>(() => WorldInitialization.PrepareCapsuleers(f.Database, f.Clock, progress: updates.Add));
        Assert.True(error is Microsoft.Data.Sqlite.SqliteException or DbUpdateException);
        Assert.NotEmpty(updates);
        Assert.True(updates[^1].CompletedSystems < updates[^1].TotalSystems);
        long[] completed;
        using (WorldDbContext world = new(f.Database.Path))
        {
            completed = world.Set<SavedCapsuleerAgent>().OrderBy(a => a.PilotId).Select(a => a.PilotId).ToArray();
            Assert.NotEmpty(completed);
            Assert.DoesNotContain(world.Set<SavedCapsuleerAgent>(), a => a.CommunitySystemId == Third);
            Assert.DoesNotContain(world.Set<SavedCapsuleerPopulation>(), community => community.SystemId == Third);
            world.Database.ExecuteSqlRaw("DROP TRIGGER reject_global_expansion");
        }
        string assets = Assets(f, completed);
        var retried = WorldInitialization.PrepareCapsuleers(new(f.Database.Path), f.Clock);
        Assert.Equal(0, retried.PendingCommunities);
        Assert.Equal(assets, Assets(f, completed));
        Assert.Equal(0, WorldInitialization.PrepareCapsuleers(new(f.Database.Path), f.Clock).CreatedCommunities);
    }

    [Fact]
    public void GlobalPreparationCanBeBoundedAndCancelledAndUnchangedRunsDoNoFittingWork()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        SetPolicy(f, new(Miners: false, TrafficMultiplier: 2, GateVisitors: 0));
        WorldInitialization.Initialize(f.Database, f.Clock);
        var first = new CapsuleerProvisioner(f.Database, f.Clock).Reconcile(maximumCommunities: 1);
        Assert.True(first.PendingCommunities > 0);
        int remaining = first.PendingCommunities;
        for (int turn = 0; turn < first.RegisteredSystems && remaining > 0; turn++)
        {
            remaining = new CapsuleerProvisioner(new(f.Database.Path), f.Clock).Reconcile(maximumCommunities: 1).PendingCommunities;
        }
        Assert.Equal(0, remaining);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        List<CapsuleerPreparationProgress> updates = [];
        Assert.Throws<OperationCanceledException>(() =>
            WorldInitialization.PrepareCapsuleers(f.Database, f.Clock, cancellation.Token, updates.Add)
        );
        Assert.Empty(updates);
        using var metrics = SessionPerformance.Begin("cached global preparation", _ => { }, force: true)!;
        var unchanged = WorldInitialization.PrepareCapsuleers(f.Database, f.Clock);
        Assert.Equal(0, unchanged.CreatedCommunities);
        Assert.Equal(0, unchanged.PendingCommunities);
        Assert.Equal(0, metrics.FittingCalculations);
        Assert.InRange(metrics.Commands, 1, 16);
        Assert.InRange(metrics.Connections, 1, 4);
    }

    [Fact]
    public void GlobalStartupKeepsImportedIdentitiesUniqueAcrossCommunitiesAndRetriesSnapshotChanges()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        CapsuleerProvisioningTests.AddSystem(f.Database, Third);
        TrafficSnapshotTests.Install(
            f.Database,
            TrafficSnapshotTests.Profile(900000001, "Imported First"),
            TrafficSnapshotTests.Profile(900000002, "Imported Second") with
            {
                KillId = 100000002,
            }
        );
        Restart(f, new(Miners: false, TrafficMultiplier: 2, GateVisitors: 0));
        long[] original;
        using (WorldDbContext world = new(f.Database.Path))
        {
            Assert.Equal(2, world.Set<SavedTrafficIdentity>().Count());
            Assert.Equal(2, world.Set<SavedTrafficIdentity>().Select(identity => identity.CharacterId).Distinct().Count());
            original = world
                .Set<SavedTrafficIdentity>()
                .OrderBy(identity => identity.PilotId)
                .Select(identity => identity.PilotId)
                .ToArray();
        }
        string assets = Assets(f, original);
        TrafficSnapshotTests.Install(f.Database, TrafficSnapshotTests.Profile(900000003, "Imported Third") with { KillId = 100000003 });
        Assert.Equal(0, WorldInitialization.PrepareCapsuleers(f.Database, f.Clock).PendingCommunities);
        Assert.Equal(assets, Assets(f, original));
        using WorldDbContext saved = new(f.Database.Path);
        Assert.Equal(
            original,
            saved.Set<SavedTrafficIdentity>().OrderBy(identity => identity.PilotId).Select(identity => identity.PilotId)
        );
    }

    [Fact]
    public void MissingSupportedFitsRemainDeferredAndNewSnapshotsFillOnlyMissingSlots()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                UPDATE SourceRecord SET Data='{{}}' WHERE Dataset='client/types' AND Key IN
                    (SELECT CAST(t.Id AS TEXT) FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE g.CategoryId=25)
                """
            );
        }
        var first = TrafficSnapshotTests.Profile();
        TrafficSnapshotTests.Install(f.Database, first);
        SetPolicy(f, new(Miners: false, TrafficMultiplier: 2, GateVisitors: 0));
        WorldInitialization.Initialize(f.Database, f.Clock);
        List<CapsuleerPreparationProgress> updates = [];
        var preparation = WorldInitialization.PrepareCapsuleers(f.Database, f.Clock, progress: updates.Add);
        Assert.True(preparation.PendingCommunities > 0);
        Assert.Equal(preparation.RegisteredSystems, updates[^1].CompletedSystems);
        Assert.Equal(preparation.PendingCommunities, updates[^1].DeferredSystems);
        long pilot;
        using (WorldDbContext world = new(f.Database.Path))
        {
            pilot = Assert.Single(world.Set<SavedTrafficIdentity>()).PilotId;
        }
        string assets = Assets(f, [pilot]);
        TrafficSnapshotTests.Install(
            f.Database,
            first,
            TrafficSnapshotTests.Profile(900000002, "New Supported Fit") with
            {
                KillId = 100000002,
            }
        );
        WorldInitialization.PrepareCapsuleers(f.Database, f.Clock);
        Assert.Equal(assets, Assets(f, [pilot]));
        using WorldDbContext saved = new(f.Database.Path);
        Assert.Equal(2, saved.Set<SavedTrafficIdentity>().Count());
        Assert.Equal(2, saved.Set<SavedTrafficIdentity>().Select(identity => identity.CharacterId).Distinct().Count());
    }

    [Fact]
    public void NewlyPreparedGlobalTrafficMovesWhileOnlyAnUnrelatedSystemIsObserved()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        Restart(f, new(Miners: false, TrafficMultiplier: 2, GateVisitors: 0));
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        using var observer = host.GetRequiredService<IHumanPresence>().Select(90000001, 30000144);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        var before = scheduler.Jobs().ToDictionary(job => job.PilotId);
        Assert.NotEmpty(before);
        for (int i = 0; i < 35; i++)
        {
            f.Clock.Seconds++;
            host.GetRequiredService<CapsuleerMaintenance>().Tick();
        }
        Assert.Contains(scheduler.Jobs(), job => job.Sequence > before[job.PilotId].Sequence);
        Assert.Equal(0, host.GetRequiredService<CapsuleerScene>().MaterializedPilots);
    }

    [Theory]
    [InlineData(0, 1, 0, TrafficJourneyKind.Regional)]
    [InlineData(0, 1, 1, TrafficJourneyKind.Local)]
    [InlineData(2, 4, 4, TrafficJourneyKind.LongDistance)]
    public void InitialHomeJourneyRespectsStationAvailabilityAndBusySystemRoles(
        int slot,
        int gates,
        int stations,
        TrafficJourneyKind expected
    ) => Assert.Equal(expected, TrafficJourneyRules.HomeKind(slot, gates, stations));

    private sealed class NoLoot : IShipLootRoll
    {
        public bool Survives() => false;
    }

    private static void Restart(CapsuleerHaulerTests.Fixture f, CapsuleerPopulationPolicy policy)
    {
        SetPolicy(f, policy);
        WorldInitialization.Initialize(f.Database, f.Clock);
        Assert.Equal(0, WorldInitialization.PrepareCapsuleers(f.Database, f.Clock).PendingCommunities);
    }

    private static string Assets(CapsuleerHaulerTests.Fixture f, long[] pilots)
    {
        using WorldDbContext world = new(f.Database.Path);
        return JsonSerializer.Serialize(
            new
            {
                Pilots = world
                    .Pilots.Where(p => pilots.Contains(p.Id))
                    .OrderBy(p => p.Id)
                    .Select(p => new
                    {
                        p.Id,
                        p.Name,
                        p.ActiveShipId,
                    })
                    .ToArray(),
                Ships = world
                    .Ships.Where(ship => pilots.Contains(ship.OwnerId))
                    .OrderBy(ship => ship.Id)
                    .Select(ship => new
                    {
                        ship.Id,
                        ship.TypeId,
                        ship.OwnerId,
                        ship.Name,
                    })
                    .ToArray(),
                Items = world
                    .Set<OwnedItem>()
                    .Where(item => pilots.Contains(item.OwnerId))
                    .OrderBy(item => item.Id)
                    .Select(item => new
                    {
                        item.Id,
                        item.TypeId,
                        item.Quantity,
                        item.LocationId,
                        item.FlagId,
                    })
                    .ToArray(),
                Wallets = world
                    .Set<PilotWallet>()
                    .Where(wallet => pilots.Contains(wallet.PilotId))
                    .OrderBy(wallet => wallet.PilotId)
                    .Select(wallet => new { wallet.PilotId, wallet.Cents })
                    .ToArray(),
                Losses = world.Set<ShipLoss>().Where(loss => pilots.Contains(loss.PilotId)).OrderBy(loss => loss.ShipId).ToArray(),
            }
        );
    }

    private static void SetPolicy(CapsuleerHaulerTests.Fixture f, CapsuleerPopulationPolicy policy)
    {
        WorldConfigurationStore settings = new(f.Database, f.Clock);
        var state = settings.Read();
        settings.Save(
            state.Generation,
            state.Active.Draft with
            {
                Settings = state.Active.Draft.Settings with { Capsuleers = policy },
            },
            "test"
        );
    }
}
