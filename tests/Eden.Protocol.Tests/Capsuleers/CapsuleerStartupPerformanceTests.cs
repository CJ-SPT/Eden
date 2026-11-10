using Eden.Data.Catalog.Capsuleers;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerStartupPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void PreparedTrafficPoolsReuseCompatibleProfilesAcrossSystemsAndSlots()
    {
        const int profiles = 10000;
        var template = TrafficSnapshotTests.Profile();
        TrafficSnapshot snapshot = new(
            null,
            Enumerable
                .Range(0, profiles)
                .Select(index =>
                    template with
                    {
                        CharacterId = template.CharacterId + index,
                        KillId = template.KillId + index,
                        Name = $"Traffic Pilot {index}",
                    }
                )
                .ToArray()
        );
        int checks = 0;
        var pools = snapshot.PrepareCandidates(template.RegionId, template.SecurityBand, Compatible, p => p.HullTypeId);
        _ = pools.Candidates(42, 30000141, 0).First();
        Assert.Equal(profiles, checks);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int slot = 0; slot < 100; slot++)
        {
            var selected = pools.Candidates(42, 30000141 + slot, slot).First();
            Assert.InRange(selected.CharacterId, template.CharacterId, template.CharacterId + profiles - 1);
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 1, 100000);
        Assert.Equal(profiles, checks);

        bool Compatible(TrafficProfile profile)
        {
            checks++;
            return profile.HullTypeId == template.HullTypeId;
        }
    }

    [Fact]
    public void TwofoldGlobalPreparationSharesCatalogConnectionsAndPreservesCachedRestart()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        var settings = new WorldConfigurationStore(f.Database, f.Clock);
        var state = settings.Read();
        settings.Save(
            state.Generation,
            state.Active.Draft with
            {
                Settings = state.Active.Draft.Settings with { Capsuleers = new(Miners: false, TrafficMultiplier: 2) },
            },
            "startup test"
        );
        WorldConfigurationStore.Activate(f.Database);
        WorldInitialization.Initialize(f.Database, f.Clock);
        using (var metrics = SessionPerformance.Begin("twofold fixture preparation", output.WriteLine, force: true)!)
        {
            var turn = WorldInitialization.PrepareCapsuleers(f.Database, f.Clock);
            Assert.True(turn.CreatedCommunities > 0);
            Assert.Equal(0, turn.PendingCommunities);
            Assert.True(metrics.Commands > 0);
            Assert.True(metrics.FittingCalculations > 0);
            Assert.InRange(metrics.Connections, 1, 4);
        }
        using WorldDbContext saved = new(f.Database.Path);
        long[] pilots = saved.Set<SavedCapsuleerAgent>().OrderBy(a => a.PilotId).Select(a => a.PilotId).ToArray();
        using (var metrics = SessionPerformance.Begin("cached fixture preparation", output.WriteLine, force: true)!)
        {
            Assert.Equal(0, WorldInitialization.PrepareCapsuleers(new(f.Database.Path), f.Clock).CreatedCommunities);
            Assert.Equal(0, metrics.FittingCalculations);
            Assert.InRange(metrics.Commands, 1, 16);
            Assert.InRange(metrics.Connections, 1, 4);
        }
        Assert.Equal(pilots, saved.Set<SavedCapsuleerAgent>().OrderBy(a => a.PilotId).Select(a => a.PilotId).ToArray());
    }

    [Fact]
    public void CopiedSaveMeasuresBoundedTwofoldExpansion()
    {
        string? source = Environment.GetEnvironmentVariable("EDEN_STARTUP_PERFORMANCE_SOURCE");
        if (source is null)
        {
            return;
        }
        var directory = TestDirectory.Create("population-startup-copy-");
        try
        {
            WorldDatabase database = new(Path.Combine(directory.FullName, "world.sqlite"));
            File.Copy(new WorldDatabase(source).CatalogPath, database.CatalogPath);
            string traffic = Path.Combine(Path.GetDirectoryName(source)!, "traffic.sqlite");
            if (File.Exists(traffic))
            {
                File.Copy(traffic, Path.Combine(directory.FullName, "traffic.sqlite"));
            }
            using (SqliteConnection original = new($"Data Source={source};Mode=ReadOnly;Pooling=False"))
            using (SqliteConnection target = new($"Data Source={database.Path};Pooling=False"))
            {
                original.Open();
                target.Open();
                original.BackupDatabase(target);
            }
            TestClock clock = new();
            var settings = new WorldConfigurationStore(database, clock);
            var state = settings.Read();
            settings.Save(
                state.Generation,
                state.Active.Draft with
                {
                    Settings = state.Active.Draft.Settings with { Capsuleers = new(TrafficMultiplier: 2) },
                },
                "startup performance test"
            );
            WorldConfigurationStore.Activate(database);
            using (WorldDbContext world = new(database.Path))
            {
                // Mirror the additive startup migration without unrelated copied-save reconciliation.
                InventoryLookupSchema.Upgrade(world);
            }
            int savedPilots;
            using (WorldDbContext world = new(database.Path))
            {
                savedPilots = world.Set<SavedCapsuleerAgent>().Count();
            }
            using (var ready = SessionPerformance.Begin("population login readiness", output.WriteLine, force: true)!)
            {
                var queued = WorldInitialization.PrepareCapsuleers(database, clock, maximumCommunities: 0);
                Assert.Equal(0, queued.CreatedCommunities);
                Assert.Equal(0, ready.FittingCalculations);
                output.WriteLine($"Login readiness: {queued.RegisteredSystems} systems, {queued.PendingCommunities} queued.");
            }
            using (WorldDbContext world = new(database.Path))
            {
                Assert.Equal(savedPilots, world.Set<SavedCapsuleerAgent>().Count());
            }
            int systems = int.TryParse(Environment.GetEnvironmentVariable("EDEN_STARTUP_PERFORMANCE_SYSTEMS"), out int requested)
                ? requested
                : 12;
            Assert.InRange(systems, 1, 128);
            // Select the same incomplete rosters in each replay of a frozen source copy.
            using (var catalogScope = new CatalogReadScope().Begin())
            using (WorldDbContext world = new(database.Path))
            using (CatalogDbContext catalog = new(database.CatalogPath))
            {
                long seed = world.Set<SavedCapsuleerProvisioning>().Single().WorldSeed;
                string fingerprint = CapsuleerProvisioner.Fingerprint(database, catalog, seed, database.Settings.Capsuleers);
                var rosters = world.Set<SavedCapsuleerAgent>().AsNoTracking().ToArray().ToLookup(a => a.CommunitySystemId);
                var selected = catalog
                    .Definitions.Systems.Keys.Order()
                    .Where(system =>
                    {
                        var plan = CapsuleerTrafficRoutes.Read(catalog, system);
                        int count = CapsuleerEncounterRules.Haulers(
                            seed,
                            system,
                            plan.GateCount,
                            plan.StationCount,
                            database.Settings.Capsuleers
                        );
                        return plan.Routes.Length > 0
                            && count > rosters[system].Count(a => a.Career == CapsuleerCareer.Hauling && a.Slot < count);
                    })
                    .Take(systems)
                    .ToArray();
                Assert.Equal(systems, selected.Length);
                world
                    .Set<SavedCapsuleerSystem>()
                    .ExecuteUpdate(setters =>
                        setters
                            .SetProperty(s => s.CatalogFingerprint, fingerprint)
                            .SetProperty(s => s.PolicyVersion, CapsuleerProvisioningRules.Version)
                    );
                world
                    .Set<SavedCapsuleerSystem>()
                    .Where(s => selected.Contains(s.SystemId))
                    .ExecuteUpdate(setters => setters.SetProperty(s => s.CatalogFingerprint, ""));
            }
            int before;
            using (WorldDbContext world = new(database.Path))
            {
                before = world.Set<SavedCapsuleerAgent>().Count();
            }
            using (var metrics = SessionPerformance.Begin("bounded twofold startup", output.WriteLine, force: true)!)
            {
                var turn = new CapsuleerProvisioner(database, clock).Reconcile(maximumCommunities: systems);
                Assert.Equal(0, turn.PendingCommunities);
                Assert.Equal(systems, turn.CreatedCommunities);
            }
            using WorldDbContext saved = new(database.Path);
            int after = saved.Set<SavedCapsuleerAgent>().Count();
            Assert.True(after > before);
            output.WriteLine($"Saved pilots: {before} -> {after}");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
