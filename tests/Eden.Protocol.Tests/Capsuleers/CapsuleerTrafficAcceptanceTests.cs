using System.Diagnostics;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Flight;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerTrafficAcceptanceTests(ITestOutputHelper output)
{
    private const int FreshBusySystem = 30000141;
    private const int FreshQuietSystem = 31000998;
    private const int JitaSystem = 30000142;
    private const int ObservationSeconds = 120;

    private sealed record Identity(long PilotId, long FounderShipId, long ActiveShipId);

    [Fact]
    public async Task FreshTrafficProducesSharedStationAndGateContactsWhileTheQuietControlStaysEmpty()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false);
        CapsuleerProvisioningTests.AddSystem(f.Database, FreshQuietSystem);
        const int ExtraBusyCorridors = 4;
        for (int corridor = 0; corridor < ExtraBusyCorridors; corridor++)
        {
            CapsuleerProvisioningTests.AddSystem(f.Database, 31001000 + corridor);
        }
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "UPDATE SourceRecord SET Data='{{\"security\":0.1}}' WHERE Dataset='client/solarsystemcontent' AND Key='31000998'"
            );
        }
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            long human = Human(host, f.Database).PilotId;
            using (host.GetRequiredService<IHumanPresence>().Select(human, FreshQuietSystem))
            {
                host.GetRequiredService<CapsuleerMaintenance>().Tick();
                Assert.Empty(host.GetRequiredService<ICapsuleerPopulation>().Agents(FreshQuietSystem));
                Assert.Equal(0, host.GetRequiredService<ICapsuleerScene>().MaterializedPilots);
                output.WriteLine($"Quiet control {FreshQuietSystem}: zero agents and zero materialized scenes.");
            }
            await MeasureBusySystem(host, f.Database, f.Clock, FreshBusySystem, "fresh fixture");
        }
        var expected = Identities(f.Database);
        WorldInitialization.Initialize(f.Database, f.Clock);
        using var reopened = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        Assert.Equal(expected, Identities(f.Database));
        using var selected = reopened.GetRequiredService<IHumanPresence>().Select(Human(reopened, f.Database).PilotId, FreshBusySystem);
        Assert.Equal(expected, Identities(f.Database));
        output.WriteLine($"Fresh reopen retained {expected.Length} pilot/founder/active-ship identities.");
    }

    [Fact]
    public async Task InstalledSaveCopyUpgradesWithoutChangingAssetsAndProducesBusyTraffic()
    {
        string? source = Environment.GetEnvironmentVariable("EDEN_ENCOUNTER_SOURCE");
        if (string.IsNullOrWhiteSpace(source))
        {
            output.WriteLine("Installed-save evidence is opt-in: set EDEN_ENCOUNTER_SOURCE to an existing world.sqlite.");
            return;
        }
        var directory = TestDirectory.Create("traffic-acceptance-copy-");
        try
        {
            WorldDatabase copy = new(Path.Combine(directory.FullName, "world.sqlite"));
            string sourceCatalog = Environment.GetEnvironmentVariable("EDEN_ENCOUNTER_CATALOG") ?? new WorldDatabase(source).CatalogPath;
            File.Copy(sourceCatalog, copy.CatalogPath);
            using (SqliteConnection original = new($"Data Source={source};Mode=ReadOnly;Pooling=False"))
            using (SqliteConnection target = new($"Data Source={copy.Path};Pooling=False"))
            {
                original.Open();
                target.Open();
                original.BackupDatabase(target);
            }
            var before = Identities(copy);
            long[] wallets;
            long[] ships;
            long[] items;
            int version;
            int busyBefore;
            using (WorldDbContext world = new(copy.Path))
            {
                version = world.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single();
                busyBefore = world.Set<SavedCapsuleerAgent>().Count(a => a.CommunitySystemId == JitaSystem);
                wallets = world.Set<PilotWallet>().OrderBy(w => w.PilotId).Select(w => w.Cents).ToArray();
                ships = world.Ships.OrderBy(s => s.Id).Select(s => s.Id).ToArray();
                items = world.Set<OwnedItem>().OrderBy(i => i.Id).Select(i => i.Id).ToArray();
            }
            TestClock clock = new();
            long start = Stopwatch.GetTimestamp();
            WorldInitialization.Initialize(copy, clock);
            double migrationMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Assert.Equal(before, Identities(copy));
            using (WorldDbContext world = new(copy.Path))
            {
                Assert.Equal(wallets, world.Set<PilotWallet>().OrderBy(w => w.PilotId).Select(w => w.Cents));
                Assert.Equal(ships, world.Ships.OrderBy(s => s.Id).Select(s => s.Id));
                Assert.Equal(items, world.Set<OwnedItem>().OrderBy(i => i.Id).Select(i => i.Id));
            }
            output.WriteLine(
                $"Installed copy schema {version}->{WorldMigration.CurrentVersion}; migration {migrationMilliseconds:F2} ms; "
                    + $"preserved {before.Length} roster identities, {ships.Length} ships, "
                    + $"{items.Length} item stacks, {wallets.Length} wallets."
            );
            using (var host = ServerServices.Build(copy, clock, liveWorld: true))
            {
                await MeasureBusySystem(host, copy, clock, JitaSystem, $"installed copy, prior local roster {busyBefore}");
            }
            var after = Identities(copy);
            WorldInitialization.Initialize(copy, clock);
            Assert.Equal(after, Identities(copy));
            using var reopened = ServerServices.Build(copy, clock, liveWorld: true);
            using var selected = reopened.GetRequiredService<IHumanPresence>().Select(Human(reopened, copy).PilotId, JitaSystem);
            Assert.Equal(after, Identities(copy));
            output.WriteLine($"Installed copy reopen retained {after.Length} pilot/founder/active-ship identities.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void RememberedTrafficNeverMovesALostFounderOrItsReplacementCapsuleToAnotherSystem()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var presence = host.GetRequiredService<IHumanPresence>();
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        var founder = f.Founder();
        long human = Human(host, f.Database).PilotId;
        using (presence.Select(human, FreshBusySystem))
        {
            Assert.Equal(founder.FounderShipId, scheduler.Hauler(founder.PilotId)!.Pilot.ShipId);
        }
        long capsule;
        long balance;
        using (WorldDbContext world = new(f.Database.Path))
        using (CatalogDbContext catalog = new(f.Database.CatalogPath))
        using (var transaction = world.Database.BeginTransaction())
        {
            SqliteWorld.SetPosition(world, catalog, founder.FounderShipId, SpacePosition.Zero, FreshBusySystem, f.Clock.Now);
            var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == founder.PilotId);
            const long AttackerNpcId = 700000002;
            world.Add(
                new WorldNpc
                {
                    Id = AttackerNpcId,
                    TypeId = 601,
                    CorporationId = 1000035,
                    SystemId = FreshBusySystem,
                }
            );
            world.SaveChanges();
            ShipLossPersistence.Apply(
                world,
                catalog,
                pilot,
                AttackerNpcId,
                founder.FounderShipId,
                FreshBusySystem,
                pilot.ActiveShip.Position!,
                f.Clock.Now,
                host.GetRequiredService<IShipLootRoll>()
            );
            world.SaveChanges();
            transaction.Commit();
            capsule = pilot.ActiveShipId;
            balance = world.Set<PilotWallet>().Single(w => w.PilotId == founder.PilotId).Cents;
        }
        f.Clock.Seconds += 1200;
        using var returning = presence.Select(human, JitaSystem);
        for (int visit = 0; visit < 2; visit++)
        {
            var remembered = scheduler.Hauler(founder.PilotId)!;
            Assert.Equal(capsule, remembered.Pilot.ShipId);
            Assert.Equal(FleetDefinitions.CapsuleGroup, remembered.Pilot.ShipGroupId);
            Assert.Equal(FreshBusySystem, remembered.Pilot.SystemId);
            Assert.Null(remembered.Work.Navigation.WarpDestination);
            using WorldDbContext world = new(f.Database.Path);
            Assert.False(world.Ships.Any(s => s.Id == founder.FounderShipId));
            Assert.Equal(capsule, Assert.Single(world.Ships.Where(s => s.OwnerId == founder.PilotId)).Id);
            Assert.Equal(balance, world.Set<PilotWallet>().Single(w => w.PilotId == founder.PilotId).Cents);
            Assert.Single(world.Set<ShipLoss>().Where(s => s.ShipId == founder.FounderShipId));
            returning.Transfer(FreshBusySystem);
            returning.Transfer(JitaSystem);
        }
    }

    private async Task MeasureBusySystem(ServiceProvider host, WorldDatabase database, TestClock clock, int system, string label)
    {
        var presence = host.GetRequiredService<IHumanPresence>();
        var worker = host.GetRequiredService<CapsuleerMaintenance>();
        var population = host.GetRequiredService<ICapsuleerPopulation>();
        var space = host.GetRequiredService<SharedSpace>();
        var objects = host.GetRequiredService<ISystemObjects>();
        var human = Human(host, database);
        long start = Stopwatch.GetTimestamp();
        using var first = presence.Select(human.PilotId, system);
        worker.Refresh();
        double entryMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var identities = population.Agents(system).OrderBy(a => a.PilotId).ToArray();
        var hauling = identities.Where(a => a.Career == CapsuleerCareer.Hauling).Select(a => a.PilotId).ToHashSet();
        Assert.True(hauling.Count >= 3, $"Busy system {system} had only {hauling.Count} haulers.");
        using var second = presence.Select(human.PilotId + 1, system);
        worker.Refresh();
        Assert.Equal(identities, population.Agents(system).OrderBy(a => a.PilotId));
        var anchors = objects
            .Load(system)
            .Where(o => o.DestinationSystemId is not null || o.CategoryId == (int)InventoryCategory.Station)
            .ToArray();
        Assert.Contains(anchors, o => o.DestinationSystemId is not null);
        Assert.Contains(anchors, o => o.DestinationSystemId is null);
        HashSet<long> stationPilots = [];
        HashSet<long> gatePilots = [];
        int? firstStation = null;
        int? firstGate = null;
        const int JitaAssemblyPlant = 60003760;
        var stationAnchor =
            anchors.FirstOrDefault(o => o.Id == JitaAssemblyPlant) ?? anchors.First(o => o.CategoryId == (int)InventoryCategory.Station);
        var gateAnchor = anchors.First(o => o.DestinationSystemId is not null);
        HashSet<long> observerRoutePilots = [];
        int? firstRouteStation = null;
        int? firstRouteGate = null;
        var flightSystem = objects.GetSystem(system);
        start = Stopwatch.GetTimestamp();
        for (int secondIndex = 0; secondIndex <= ObservationSeconds; secondIndex++)
        {
            // The fixed observer path samples one station for 60 seconds, then
            // one gate for 60 seconds. Keep this distinct from the grid sweep.
            var routeAnchor = secondIndex < ObservationSeconds / 2 ? stationAnchor : gateAnchor;
            var routeViewer = human with { CurrentSystem = flightSystem, Position = routeAnchor.Position };
            var routeContacts = space
                .Peers(routeViewer)
                .Where(c => hauling.Contains(c.Pilot.PilotId))
                .Select(c => c.Pilot.PilotId)
                .ToArray();
            Assert.Equal(
                routeContacts,
                space
                    .Peers(routeViewer with { ShipId = human.ShipId + 1 })
                    .Where(c => hauling.Contains(c.Pilot.PilotId))
                    .Select(c => c.Pilot.PilotId)
            );
            observerRoutePilots.UnionWith(routeContacts);
            if (routeContacts.Length > 0)
            {
                if (routeAnchor == stationAnchor)
                {
                    firstRouteStation ??= secondIndex;
                }
                else
                {
                    firstRouteGate ??= secondIndex;
                }
            }
            foreach (var anchor in anchors)
            {
                var viewer = human with { CurrentSystem = flightSystem, Position = anchor.Position };
                var another = viewer with { ShipId = human.ShipId + 1 };
                var contacts = space.Peers(viewer).Where(c => hauling.Contains(c.Pilot.PilotId)).Select(c => c.Pilot.PilotId).ToArray();
                Assert.Equal(contacts, space.Peers(another).Where(c => hauling.Contains(c.Pilot.PilotId)).Select(c => c.Pilot.PilotId));
                if (contacts.Length == 0)
                {
                    continue;
                }
                if (anchor.DestinationSystemId is null)
                {
                    firstStation ??= secondIndex;
                    stationPilots.UnionWith(contacts);
                }
                else
                {
                    firstGate ??= secondIndex;
                    gatePilots.UnionWith(contacts);
                }
            }
            if (secondIndex < ObservationSeconds)
            {
                clock.Seconds++;
                worker.Tick();
            }
        }
        double observationMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        output.WriteLine(
            $"{label}: system {system}, {hauling.Count} local haulers, {identities.Length} total local agents; "
                + $"entry {entryMilliseconds:F2} ms; all-grid sweep first gate {firstGate?.ToString() ?? "none"} simulated s, "
                + $"first station {firstStation?.ToString() ?? "none"} simulated s; "
                + $"{gatePilots.Count} distinct gate pilots, {stationPilots.Count} distinct station pilots "
                + $"in {ObservationSeconds} simulated s; "
                + $"observation execution {observationMilliseconds:F2} ms."
        );
        output.WriteLine(
            $"Fixed two-observer path: station {stationAnchor.Id} at 0-59 simulated s, gate {gateAnchor.Id} at 60-120 simulated s; "
                + $"first station contact {firstRouteStation?.ToString() ?? "none"} s, "
                + $"first gate contact {firstRouteGate?.ToString() ?? "none"} s; {observerRoutePilots.Count} distinct pilots."
        );
        Assert.NotNull(firstGate);
        Assert.NotNull(firstStation);
        Assert.True(gatePilots.Union(stationPilots).Count() >= 2);
        Assert.NotEmpty(observerRoutePilots);
        Assert.Equal(identities, population.Agents(system).OrderBy(a => a.PilotId));
        await host.GetRequiredService<LiveWorld>().FlushAsync();
    }

    private static Identity[] Identities(WorldDatabase database)
    {
        using WorldDbContext world = new(database.Path);
        return (
            from agent in world.Set<SavedCapsuleerAgent>()
            join pilot in world.Pilots on agent.PilotId equals pilot.Id
            orderby agent.PilotId
            select new Identity(agent.PilotId, agent.FounderShipId, pilot.ActiveShipId)
        ).ToArray();
    }

    private static DockedPilot Human(ServiceProvider host, WorldDatabase database)
    {
        using WorldDbContext world = new(database.Path);
        var pilot = world.Pilots.Where(p => p.AccountId > 0).OrderBy(p => p.Id).Select(p => new { p.Id, p.AccountId }).First();
        return host.GetRequiredService<SqliteWorld>().FindPilot(pilot.AccountId, pilot.Id)!;
    }
}
