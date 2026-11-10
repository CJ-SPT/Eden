using System.Diagnostics;
using System.Globalization;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Chat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;
using Eden.Server;
using Eden.Server.Chat;
using Eden.Server.Maintenance;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerInboundGateAcceptanceTests(ITestOutputHelper output)
{
    private const int FreshReceivingSystem = 30000141;
    private const long FreshReceivingGate = 50009991;
    private const int Vattuolen = 30000173;
    private const long UoyonenGateInVattuolen = 50005333;
    private const int FirstArrivalSeconds = 300;
    private const int RepeatObservationSeconds = 3600;
    private const double ObserverClearance = 10_000;

    private sealed record Arrival(long PilotId, long ShipId, int Seconds, bool PresentAtLogin);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceivingOnlyObserversSeeRealInboundTransfersAndRepeatedArrivalAtTheWatchedGate(bool restartPending)
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false);
        if (restartPending)
        {
            await RestartPendingInbound(f.Database, f.Clock);
        }
        string label = restartPending ? "fresh fixture after pending-wait restart" : "fresh fixture";
        await ObserveGate(f.Database, f.Clock, FreshReceivingSystem, FreshReceivingGate, label);
    }

    [Fact]
    public async Task CopiedVattuolenSaveProducesInboundUoyonenArrivalsWithNoHumanAcrossTheGate()
    {
        string? source = Environment.GetEnvironmentVariable("EDEN_GATEWATCH_SOURCE");
        if (string.IsNullOrWhiteSpace(source))
        {
            output.WriteLine("Installed gate-watch evidence is opt-in: set EDEN_GATEWATCH_SOURCE to an existing world.sqlite.");
            return;
        }
        var directory = TestDirectory.Create("inbound-gate-save-copy-");
        try
        {
            WorldDatabase copy = new(Path.Combine(directory.FullName, "world.sqlite"));
            string catalog = Environment.GetEnvironmentVariable("EDEN_GATEWATCH_CATALOG") ?? new WorldDatabase(source).CatalogPath;
            File.Copy(catalog, copy.CatalogPath);
            using (SqliteConnection original = new($"Data Source={source};Mode=ReadOnly;Pooling=False"))
            using (SqliteConnection target = new($"Data Source={copy.Path};Pooling=False"))
            {
                original.Open();
                target.Open();
                original.BackupDatabase(target);
            }
            int version;
            using (WorldDbContext world = new(copy.Path))
            {
                version = world.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single();
                int localHaulers = world
                    .Set<SavedCapsuleerAgent>()
                    .Count(a => a.CommunitySystemId == Vattuolen && a.Career == CapsuleerCareer.Hauling);
                int total = world.Set<SavedCapsuleerAgent>().Count();
                output.WriteLine(
                    $"Source copy schema {version}, {total} saved agents, {localHaulers} Vattuolen haulers before gate watching."
                );
            }
            TestClock clock = new();
            WorldInitialization.Initialize(copy, clock);
            await ObserveGate(
                copy,
                clock,
                Vattuolen,
                UoyonenGateInVattuolen,
                $"copied save schema {version}->{WorldMigration.CurrentVersion}"
            );
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private async Task ObserveGate(WorldDatabase database, TestClock clock, int receivingSystem, long gateId, string label)
    {
        var (humans, gate) = PositionObservers(database, clock, receivingSystem, gateId);
        int sourceSystem = gate.DestinationSystemId!.Value;
        using var host = ServerServices.Build(database, clock, liveWorld: true);
        var ships = host.GetRequiredService<SqliteWorld>();
        var factory = host.GetRequiredService<FlightSessionFactory>();
        var worker = host.GetRequiredService<CapsuleerMaintenance>();
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        var presence = host.GetRequiredService<IHumanPresence>();
        var space = host.GetRequiredService<SharedSpace>();
        var live = host.GetRequiredService<LiveWorld>();
        var chat = host.GetRequiredService<ChatRuntime>();
        long start = Stopwatch.GetTimestamp();
        var firstPilot = FindHuman(ships, database, humans[0]);
        var secondPilot = FindHuman(ships, database, humans[1]);
        using var first = factory.Create(firstPilot);
        first.Snapshot();
        using var second = factory.Create(secondPilot);
        second.Snapshot();
        host.PrepareActivatedPopulations();
        var firstSession = Guid.NewGuid();
        var secondSession = Guid.NewGuid();
        chat.Update(firstSession, new(firstPilot.PilotId, firstPilot.Name, firstPilot.CorpId, receivingSystem, 0));
        chat.Update(secondSession, new(secondPilot.PilotId, secondPilot.Name, secondPilot.CorpId, receivingSystem, 0));
        var firstChat = chat.Authenticate(firstPilot.PilotId.ToString(CultureInfo.InvariantCulture), chat.IssueToken(firstSession));
        var secondChat = chat.Authenticate(secondPilot.PilotId.ToString(CultureInfo.InvariantCulture), chat.IssueToken(secondSession));
        ChatRoom room = new(ChatRoomKind.Local, receivingSystem);
        chat.Join(firstChat.Id, room, null);
        chat.Join(secondChat.Id, room, null);
        worker.Refresh();
        var gateWatch = host.GetRequiredService<ICapsuleerGateWatch>();
        var watched = Assert.Single(gateWatch.Read(presence.Read()), g => g.ArrivalGateId == gateId);
        Assert.Equal(sourceSystem, watched.SourceSystemId);
        double entryMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var loginShips = space.Peers(first.Pilot).Where(c => !humans.Contains(c.Pilot.PilotId)).Select(c => c.Pilot.ShipId).ToHashSet();
        var previousContacts = space.Peers(first.Pilot).ToDictionary(c => c.Pilot.ShipId);
        first.Updates.Clear();
        second.Updates.Clear();
        Dictionary<long, (long ShipId, int SystemId)> previous = [];
        HashSet<(long PilotId, long ShipId)> seenInSource = [];
        HashSet<long> firstLocalArrivals = [];
        HashSet<long> secondLocalArrivals = [];
        List<Arrival> arrivals = [];
        long populationRevision = -1;
        long[] candidates = [];
        int? departure = null;
        int gateActivations = 0;
        int departureAnimations = 0;
        Dictionary<long, bool> dockedSinceArrival = [];
        int undockedReversals = 0;
        start = Stopwatch.GetTimestamp();
        for (int seconds = 0; seconds <= RepeatObservationSeconds; seconds++)
        {
            Assert.Equal(new[] { receivingSystem }, presence.Read().ActiveSystems);
            Assert.Equal(2, presence.Read().SelectedSessions);
            if (populationRevision != live.CapsuleerRevision)
            {
                using WorldDbContext world = new(database.Path);
                candidates = world
                    .Set<SavedCapsuleerHauler>()
                    .Where(h => h.RouteSystems != null && h.RouteSystems.Contains(receivingSystem) && h.RouteSystems.Contains(sourceSystem))
                    .Select(h => h.PilotId)
                    .ToArray();
                populationRevision = live.CapsuleerRevision;
            }
            var firstContacts = space.Peers(first.Pilot).Where(c => !humans.Contains(c.Pilot.PilotId)).ToArray();
            var secondContacts = space.Peers(second.Pilot).Where(c => !humans.Contains(c.Pilot.PilotId)).ToArray();
            Assert.Equal(firstContacts.Select(c => c.Pilot.ShipId), secondContacts.Select(c => c.Pilot.ShipId));
            first.RefreshPeers();
            second.RefreshPeers();
            var firstUpdates = first.Updates.Select(u => u.Command).ToArray();
            var secondUpdates = second.Updates.Select(u => u.Command).ToArray();
            first.Updates.Clear();
            second.Updates.Clear();
            var newArrivals = firstContacts
                .Where(c => c.Arriving && (!previousContacts.TryGetValue(c.Pilot.ShipId, out var old) || !old.Arriving))
                .ToArray();
            var newDepartures = firstContacts
                .Where(c =>
                    c.DepartureGate == gateId && (!previousContacts.TryGetValue(c.Pilot.ShipId, out var old) || old.DepartureGate != gateId)
                )
                .ToArray();
            int expectedActivations = newArrivals.Length + newDepartures.Length;
            CheckGateActivations(firstUpdates, gateId, newArrivals.Length, newDepartures.Length);
            CheckGateActivations(secondUpdates, gateId, newArrivals.Length, newDepartures.Length);
            gateActivations += expectedActivations;
            departureAnimations += newDepartures.Length;
            foreach (var departing in newDepartures)
            {
                foreach (var updates in new[] { firstUpdates, secondUpdates })
                {
                    var effect = Assert.Single(updates.OfType<DestinyCommand.JumpOut>(), e => e.Id == departing.Pilot.ShipId);
                    Assert.Equal(gateId, effect.GateId);
                    Assert.Equal(sourceSystem, effect.SystemId);
                }
            }
            previousContacts = firstContacts.ToDictionary(c => c.Pilot.ShipId);
            while (firstChat.Events.TryRead(out var delivery))
            {
                if (delivery.Event is ChatEvent.Presence { Available: true } joined)
                {
                    firstLocalArrivals.Add(joined.Pilot.Id);
                }
                else if (delivery.Event is ChatEvent.Roster roster)
                {
                    firstLocalArrivals.UnionWith(roster.Pilots.Select(pilot => pilot.Id));
                }
            }
            while (secondChat.Events.TryRead(out var delivery))
            {
                if (delivery.Event is ChatEvent.Presence { Available: true } joined)
                {
                    secondLocalArrivals.Add(joined.Pilot.Id);
                }
                else if (delivery.Event is ChatEvent.Roster roster)
                {
                    secondLocalArrivals.UnionWith(roster.Pilots.Select(pilot => pilot.Id));
                }
            }
            foreach (long id in candidates)
            {
                var observation = scheduler.Hauler(id)!;
                var pilot = observation.Pilot;
                if (pilot.Position is null)
                {
                    dockedSinceArrival[id] = true;
                }
                if (
                    previous.TryGetValue(id, out var prior)
                    && prior.SystemId == receivingSystem
                    && pilot.SystemId == sourceSystem
                    && dockedSinceArrival.TryGetValue(id, out bool docked)
                    && !docked
                )
                {
                    undockedReversals++;
                }
                if (pilot.SystemId == sourceSystem && pilot.Position is not null)
                {
                    seenInSource.Add((id, pilot.ShipId));
                }
                if (
                    arrivals.FirstOrDefault() is { } earliest
                    && id == earliest.PilotId
                    && seconds > earliest.Seconds
                    && pilot.SystemId != receivingSystem
                )
                {
                    departure ??= seconds;
                }
                if (
                    seconds > 0
                    && pilot.SystemId == receivingSystem
                    && previous.TryGetValue(id, out var old)
                    && old.SystemId == sourceSystem
                )
                {
                    Assert.Equal(old.ShipId, pilot.ShipId);
                    Assert.Contains((id, pilot.ShipId), seenInSource);
                    var contact = Assert.Single(firstContacts, c => c.Pilot.PilotId == id);
                    Assert.True(contact.Arriving);
                    Assert.Contains(newArrivals, c => c.Pilot.ShipId == pilot.ShipId);
                    foreach (var updates in new[] { firstUpdates, secondUpdates })
                    {
                        Assert.Single(
                            updates.OfType<DestinyCommand.SpecialEffect>(),
                            e => e.Id == pilot.ShipId && e.Graphic == "effects.JumpIn"
                        );
                    }
                    Assert.True(contact.Pilot.Position!.DistanceTo(gate.Position) < ObserverClearance * 4);
                    Assert.Contains(secondContacts, c => c.Pilot.ShipId == pilot.ShipId && c.Arriving);
                    Assert.Contains(first.VisiblePilots(), p => p.PilotId == id && p.ShipId == pilot.ShipId);
                    Assert.Contains(second.VisiblePilots(), p => p.PilotId == id && p.ShipId == pilot.ShipId);
                    Assert.Contains(chat.Members(firstChat.Id, room), p => p.Id == id);
                    Assert.Contains(chat.Members(secondChat.Id, room), p => p.Id == id);
                    Assert.Contains(id, firstLocalArrivals);
                    Assert.Contains(id, secondLocalArrivals);
                    dockedSinceArrival[id] = false;
                    arrivals.Add(new(id, pilot.ShipId, seconds, loginShips.Contains(pilot.ShipId)));
                    output.WriteLine($"Actual inbound crossing at {seconds} simulated s: pilot {id}, same ship {pilot.ShipId}.");
                }
                previous[id] = (pilot.ShipId, pilot.SystemId);
            }
            clock.Seconds++;
            worker.Tick();
        }
        double observationMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        output.WriteLine(
            $"{label}: receive system {receivingSystem}, exact gate {gateId}, source system {sourceSystem}; "
                + $"login-grid ships {loginShips.Count}, candidates {candidates.Length}; entry {entryMilliseconds:F2} ms; "
                + $"first actual arrival {arrivals.FirstOrDefault()?.Seconds.ToString() ?? "none"} simulated s, "
                + $"first departure {departure?.ToString() ?? "none"} simulated s, actual arrivals {arrivals.Count}; "
                + $"returning login ships {arrivals.Count(a => a.PresentAtLogin)}; "
                + $"gate activations per observer {gateActivations}, departure animations {departureAnimations}; "
                + $"observation execution {observationMilliseconds:F2} ms."
        );
        output.WriteLine(
            $"One-hour traffic: arrivals={arrivals.Count}, distinct={arrivals.Select(a => a.PilotId).Distinct().Count()}, "
                + $"repeat arrivals={arrivals.Count - arrivals.Select(a => a.PilotId).Distinct().Count()}, "
                + $"undocked reversals={undockedReversals}."
        );
        Assert.NotEmpty(arrivals);
        // A restarted opening may already have delivered the first visitor onto the login grid.
        // Its next new arrival includes a station departure and the following staggered slot.
        int arrivalBudget = loginShips.Count > 1 ? FirstArrivalSeconds * 2 : FirstArrivalSeconds;
        Assert.InRange(arrivals[0].Seconds, 1, arrivalBudget);
        Assert.NotNull(departure);
        Assert.True(arrivals.Select(arrival => arrival.PilotId).Distinct().Count() >= 2);
        Assert.Equal(0, undockedReversals);
        Assert.True(departureAnimations > 0);
        Assert.Equal(0, host.GetRequiredService<ICapsuleerDiagnostics>().Read().Failures);
        var existingJobs = scheduler.Jobs().ToArray();
        for (int cycle = 0; cycle < 2; cycle++)
        {
            second.Dispose();
            Assert.Equal(1, presence.Read().SelectedSessions);
            Assert.Equal(watched, Assert.Single(gateWatch.Read(presence.Read()), g => g.ArrivalGateId == gateId));
            using var rejoined = factory.Create(secondPilot);
            rejoined.Snapshot();
            Assert.Equal(2, presence.Read().SelectedSessions);
            Assert.Equal(watched, Assert.Single(gateWatch.Read(presence.Read()), g => g.ArrivalGateId == gateId));
            Assert.Equal(existingJobs, scheduler.Jobs());
        }
        await live.FlushAsync();
        chat.Disconnect(firstChat.Id);
        chat.Disconnect(secondChat.Id);
    }

    private static DockedPilot FindHuman(SqliteWorld ships, WorldDatabase database, long id)
    {
        using WorldDbContext world = new(database.Path);
        long account = world.Pilots.Where(p => p.Id == id).Select(p => p.AccountId).Single();
        return ships.FindPilot(account, id)!;
    }

    private static (long[] Humans, StaticSpaceObject Gate) PositionObservers(
        WorldDatabase database,
        TestClock clock,
        int receivingSystem,
        long gateId
    )
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        using WorldDbContext world = new(database.Path);
        var gate = SystemObjects.Geometry(catalog, receivingSystem).Single(o => o.Id == gateId);
        var humans = world.Pilots.Where(p => p.AccountId > 0).OrderBy(p => p.Id).Select(p => p.Id).Take(2).ToArray();
        Assert.Equal(2, humans.Length);
        using var transaction = world.Database.BeginTransaction();
        foreach (long id in humans)
        {
            var saved = world.Pilots.Single(p => p.Id == id);
            // These are actual saved observer ships, not synthetic viewers
            // injected into a system-wide contact query.
            var position = gate.Position + new SpacePosition(gate.Radius + ObserverClearance, 0, 0);
            SqliteWorld.SetPosition(world, catalog, saved.ActiveShipId, position, receivingSystem, clock.Now);
        }
        transaction.Commit();
        return (humans, gate);
    }

    private async Task RestartPendingInbound(WorldDatabase database, TestClock clock)
    {
        var (humans, gate) = PositionObservers(database, clock, FreshReceivingSystem, FreshReceivingGate);
        long pilot;
        long ship;
        using (var host = ServerServices.Build(database, clock, liveWorld: true))
        {
            var factory = host.GetRequiredService<FlightSessionFactory>();
            var ships = host.GetRequiredService<SqliteWorld>();
            using var first = factory.Create(FindHuman(ships, database, humans[0]));
            first.Snapshot();
            using var second = factory.Create(FindHuman(ships, database, humans[1]));
            second.Snapshot();
            host.PrepareActivatedPopulations();
            clock.Seconds += 10;
            host.GetRequiredService<CapsuleerMaintenance>().Tick();
            using WorldDbContext world = new(database.Path);
            long slot = Math.Min(gate.Id, gate.DestinationGateId!.Value) + TrafficJourneyRules.VisitorSlotStride;
            var actor = world.Set<SavedCapsuleerAgent>().Single(a => a.Career == CapsuleerCareer.Hauling && a.Slot == slot);
            pilot = actor.PilotId;
            ship = actor.FounderShipId;
            var observation = host.GetRequiredService<ICapsuleerScheduler>().Hauler(pilot)!;
            Assert.Equal(HaulerActivity.Docked, observation.Work.Activity);
            Assert.Equal(gate.DestinationSystemId, observation.Pilot.SystemId);
            Assert.Equal(ship, observation.Pilot.ShipId);
            await host.GetRequiredService<LiveWorld>().FlushAsync();
        }
        clock.Seconds += 600;
        WorldInitialization.Initialize(database, clock);
        using WorldDbContext reopened = new(database.Path);
        var saved = reopened.Set<SavedCapsuleerAgent>().Single(a => a.PilotId == pilot);
        Assert.Equal(ship, saved.FounderShipId);
        Assert.Equal(ship, reopened.Pilots.Where(p => p.Id == pilot).Select(p => p.ActiveShipId).Single());
        output.WriteLine($"Pending source-side actor {pilot}, ship {ship}, retained through restart and 600 offline seconds.");
    }

    private static void CheckGateActivations(IReadOnlyList<DestinyCommand> updates, long gateId, int arrivals, int departures)
    {
        var activations = updates.OfType<DestinyCommand.GateActivity>().ToArray();
        Assert.Equal(arrivals + departures, activations.Length);
        Assert.Equal(arrivals, activations.Count(a => a.Arriving));
        Assert.Equal(departures, activations.Count(a => !a.Arriving));
        Assert.All(
            activations,
            a =>
            {
                Assert.Equal(gateId, a.GateId);
                var wire = Assert.IsType<object?[]>(a.ToBlue());
                Assert.Equal("OnClientControllerEvent", wire[0]);
                Assert.Equal(
                    new object?[] { gateId, null, a.Arriving ? "Arrival" : "Departure", 1.0, null },
                    Assert.IsType<object?[]>(wire[1])
                );
            }
        );
    }
}
