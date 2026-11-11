using System.Runtime.CompilerServices;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime;

internal sealed partial class LiveWorld
{
    internal static T Execute<T>(string path, Func<T> action, string operation)
    {
        return Find(path) is { } live ? live.Run(action, operation) : action();
    }

    internal static T ExecuteForShip<T>(string path, long ship, Func<T> action, string operation)
    {
        if (Find(path) is not { } live || live.CurrentSystem is not null)
        {
            return Execute(path, action, operation);
        }
        if (!live._shipSystems.TryGetValue(ship, out int system))
        {
            using var persistence = live.EnterPersistence("ship membership");
            using WorldDbContext world = new(path) { IsResidentProjection = true };
            var membership = world
                .Ships.Where(s => s.Id == ship)
                .Select(s => new { s.StationId, System = s.Position == null ? null : s.Position.SystemId })
                .SingleOrDefault();
            if (membership is null)
            {
                return live.Run(action, operation);
            }
            system = membership.System ?? live._database.Catalog.FindStation(membership.StationId)!.SolarSystemId;
            live.TrackShip(ship, system);
        }
        using var context = live.UseSystem(system);
        return live.Run(action, operation);
    }

    internal static T Execute<T>(string path, int system, Func<T> action, string operation)
    {
        if (Find(path) is not { } live)
        {
            return action();
        }
        using var context = live.UseSystem(system);
        return live.Run(action, operation);
    }

    internal static void Execute(string path, int system, Action action, string operation) =>
        Execute(
            path,
            system,
            () =>
            {
                action();
                return true;
            },
            operation
        );

    internal LiveCombatRuntime? CombatForPilot(long pilot)
    {
        if (CurrentSystem is not null)
        {
            return Combat;
        }
        return _systems.Get(PilotSystem(pilot)).State.Combat;
    }

    internal LiveCombatRuntime? CombatForSystem(int system) => _systems.Get(system).State.Combat;

    internal LiveShipHeat HeatForKnownShip(long ship) =>
        _heatOwners.TryGetValue(ship, out var owner) ? owner.Heat
        : _shipSystems.TryGetValue(ship, out int system) ? _systems.Get(system).State.Heat
        : Heat;

    internal LiveShipHeat HeatForKnownPilot(long pilot) =>
        _pilotSystems.TryGetValue(pilot, out int system) ? _systems.Get(system).State.Heat : Heat;

    internal int? KnownShipSystem(long ship) => _shipSystems.TryGetValue(ship, out int system) ? system : null;

    internal void PublishHeat(long pilot, long ship, Eden.Gameplay.Thermal.ShipHeatSnapshot state)
    {
        if (_shipSystems.TryGetValue(ship, out int system))
        {
            using var context = UseSystem(system);
            using var ownership = EnterSimulation("committed heat", system);
            Heat.PublishCommitted(pilot, ship, state);
        }
        else
        {
            Heat.PublishCommitted(pilot, ship, state);
        }
    }

    internal bool IsSharedHeat(LiveShipHeat heat) => ReferenceEquals(_shared.Heat, heat);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _sharedHeatSystems = new();

    internal void SharedHeatLoaded(int system) => _sharedHeatSystems[system] = 0;

    internal LiveCombatRuntime? CombatForShip(long ship)
    {
        if (CurrentSystem is not null)
        {
            return Combat;
        }
        return _shipSystems.TryGetValue(ship, out int system) ? CombatForSystem(system) : _shared.Combat;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> _shipSystems = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> _npcSystems = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> _shipOwners = new();

    internal void TrackShip(long ship, int system, long pilot = 0)
    {
        _shipSystems[ship] = system;
        if (pilot != 0)
        {
            _pilotSystems[pilot] = system;
            _shipOwners[ship] = pilot;
        }
    }

    internal void TrackNpc(long npc, int system) => _npcSystems[npc] = system;

    internal void BeforeSharedSql(string sql, bool readsResidentNpcTimer)
    {
        using var context = UseShared();
        _shared.Combat?.BeforeSql(sql, readsResidentNpcTimer);
        _shared.Heat.BeforeSql(sql);
        if (sql.Contains("WorldWreck", StringComparison.Ordinal))
        {
            _shared.Tractors.Flush();
        }
    }

    internal void ProjectShared(TransactionParticipants? participants)
    {
        using var context = UseShared();
        _shared.Combat?.Flush(participants);
        _shared.Heat.Flush(participants);
        _shared.Tractors.Flush(participants);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> _pilotSystems = new();

    private int PilotSystem(long pilot) =>
        _pilotSystems.GetOrAdd(
            pilot,
            id =>
            {
                using var ownership = EnterShared("pilot membership");
                using WorldDbContext world = new(_path) { IsResidentProjection = true };
                var row = world
                    .Pilots.Where(p => p.Id == id)
                    .Select(p => new
                    {
                        p.ActiveShipId,
                        p.ActiveShip.Position,
                        p.ActiveShip.StationId,
                    })
                    .Single();
                int system = row.Position?.SystemId ?? _database.Catalog.FindStation(row.StationId)!.SolarSystemId;
                TrackShip(row.ActiveShipId, system, id);
                return system;
            }
        );

    internal static void Execute(string path, Action action, string operation)
    {
        if (Find(path) is { } live)
        {
            live.Run(action, operation);
        }
        else
        {
            action();
        }
    }

    private readonly AsyncLocal<int?> _currentSystem = new();
    private readonly ThreadLocal<int> _executionDepth = new();
    private readonly ThreadLocal<Stack<int>> _heldSystems = new(() => new());
    private readonly SystemRuntimeRegistry<LiveSystemState> _systems;
    private readonly LiveSystemState _shared;

    internal int? CurrentSystem => _currentSystem.Value;
    private LiveSystemState Current => CurrentSystem is int system ? _systems.Get(system).State : _shared;

    internal void AssignCapsuleer(
        long pilot,
        int? previous,
        int system,
        Eden.World.Capsuleers.CapsuleerMiner? miner,
        Eden.World.Capsuleers.CapsuleerHauler? hauler
    )
    {
        if (!_ownershipMonitor.IsEntered)
        {
            throw new InvalidOperationException("Moving a capsuleer runtime requires persistence coordination.");
        }
        if (previous is int old && old != system)
        {
            var prior = _systems.Get(old);
            using var ownership = prior.Enter("capsuleer departure");
            prior.State.Miners.Remove(pilot);
            prior.State.Haulers.Remove(pilot);
        }
        var entry = _systems.Get(system);
        using var arrival = entry.Enter("capsuleer admission");
        entry.State.Miners.Remove(pilot);
        entry.State.Haulers.Remove(pilot);
        if (miner is not null)
        {
            entry.State.Miners[pilot] = miner;
        }
        if (hauler is not null)
        {
            entry.State.Haulers[pilot] = hauler;
        }
    }

    internal IDisposable UseSystem(int system)
    {
        int? previous = _currentSystem.Value;
        _currentSystem.Value = system;
        return new SystemContext(this, previous);
    }

    private IDisposable UseShared()
    {
        int? previous = _currentSystem.Value;
        _currentSystem.Value = null;
        return new SystemContext(this, previous);
    }

    internal IDisposable EnterShared(string operation = "shared world")
    {
        var context = UseShared();
        bool nested = _ownershipMonitor.IsEntered;
        try
        {
            var persistence = EnterPersistence(operation);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (Volatile.Read(ref _stopping) && !nested)
                {
                    throw new InvalidOperationException("The resident world is shutting down.");
                }
                AdvanceEffects(_shared);
                return new CoordinatedLease(context, persistence);
            }
            catch
            {
                persistence.Dispose();
                throw;
            }
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    internal IDisposable? TryEnterShared(string operation)
    {
        if (_heldSystems.Value!.Count != 0 && !_ownershipMonitor.IsEntered)
        {
            return null;
        }
        if (_ownershipMonitor.TryEnter(operation) is not { } persistence)
        {
            return null;
        }
        // Persistence serializes scheduler metadata and publication. Each actual
        // observation/scene operation enters its participating system separately.
        // Acquiring dormant runtimes here stalls unrelated resident frames.
        var context = UseShared();
        return new CoordinatedLease(context, persistence);
    }

    internal IDisposable? TryEnterSystems(string operation, IReadOnlyCollection<int> systems)
    {
        if (!_ownershipMonitor.IsEntered)
        {
            throw new InvalidOperationException("Presentation systems require persistence coordination first.");
        }
        return _systems.TryEnter(systems, operation);
    }

    private sealed class SystemContext(LiveWorld world, int? previous) : IDisposable
    {
        public void Dispose() => world._currentSystem.Value = previous;
    }

    // SQL is discovered before opening a connection. Unwind the complete resident
    // operation, then enter persistence first and rerun its typed validation. No
    // connection can upgrade a retained system lease to persistence ownership.
    internal T Run<T>(Func<T> operation, [CallerMemberName] string name = "")
    {
        if (_executionDepth.Value != 0 || _ownershipMonitor.IsEntered || CurrentSystem is null)
        {
            return operation();
        }
        _executionDepth.Value++;
        try
        {
            try
            {
                using var ownership = EnterSimulation(name, CurrentSystem.Value, advanceEffects: false);
                using var changes = new ResidentChanges();
                AdvanceEffects(Current);
                var result = operation();
                changes.Commit();
                return result;
            }
            catch (PersistenceRequiredException request)
            {
                var entry = _systems.Get(CurrentSystem!.Value);
                long revision = entry.Revision;
                using var persistence = EnterCoordinated(name, request.Participants);
                using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase(
                    entry.Revision == revision ? "resident.revalidate" : "resident.revalidate.changed"
                );
                // Re-enter the typed operation from its boundary. All memberships,
                // deadlines and mutable resident inputs are read again, including
                // changes published while this request waited for persistence.
                return operation();
            }
        }
        finally
        {
            _executionDepth.Value--;
        }
    }

    internal void Run(Action operation, [CallerMemberName] string name = "") =>
        Run(
            () =>
            {
                operation();
                return true;
            },
            name
        );

    internal sealed class PersistenceRequiredException(TransactionParticipants? participants = null) : Exception
    {
        internal TransactionParticipants? Participants { get; } = participants;
    }

    internal IDisposable EnterPersistence(string operation, TransactionParticipants? participants = null)
    {
        if (!_ownershipMonitor.IsEntered && _heldSystems.Value!.Count != 0)
        {
            throw new PersistenceRequiredException(participants);
        }
        if (!_ownershipMonitor.IsEntered && Volatile.Read(ref _checkpointCopying))
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var lease = Acquire(operation);
            RecordPersistenceWait(operation, System.Diagnostics.Stopwatch.GetElapsedTime(started));
            return lease;
        }
        return Acquire(operation);
    }

    private IDisposable EnterCoordinated(string operation, TransactionParticipants? participants = null)
    {
        var persistence = EnterPersistence(operation);
        try
        {
            var systems = _systems.Enter(ParticipantSystems(participants), operation);
            return new CoordinatedLease(persistence, systems);
        }
        catch
        {
            persistence.Dispose();
            throw;
        }
    }

    private sealed class CoordinatedLease(IDisposable persistence, IDisposable systems) : IDisposable
    {
        public void Dispose()
        {
            systems.Dispose();
            persistence.Dispose();
        }
    }

    private IDisposable EnterSimulation(string operation, int system, bool advanceEffects = true)
    {
        if (!_ownershipMonitor.IsEntered && _heldSystems.Value!.TryPeek(out int held) && held != system)
        {
            throw new InvalidOperationException("Cross-system operations must enter through the persistence coordinator.");
        }
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (Volatile.Read(ref _stopping) && !_systems.Get(system).Ownership.IsEntered && !_ownershipMonitor.IsEntered)
        {
            throw new InvalidOperationException("The resident world is shutting down.");
        }
        var entry = _systems.Get(system);
        var lease = new SimulationLease(this, entry.Enter(operation), system);
        try
        {
            entry.State.SimulationEntered = true;
            if (_sharedHeatSystems.TryRemove(system, out _))
            {
                if (!_ownershipMonitor.IsEntered)
                {
                    SharedHeatLoaded(system);
                    throw new PersistenceRequiredException(new(systems: [system]));
                }
                try
                {
                    _shared.Heat.TransferSystemTo(entry.State.Heat, system);
                }
                catch
                {
                    SharedHeatLoaded(system);
                    throw;
                }
            }
            while (entry.State.PendingChanges.TryPeek(out var changes))
            {
                ObserveState(entry.State, changes);
                entry.State.PendingChanges.TryDequeue(out _);
            }
            if (advanceEffects && (_executionDepth.Value != 0 || _ownershipMonitor.IsEntered))
            {
                AdvanceEffects(entry.State);
            }
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private sealed class SimulationLease : IDisposable
    {
        private readonly LiveWorld _world;
        private IDisposable? _ownership;

        internal SimulationLease(LiveWorld world, IDisposable ownership, int system)
        {
            _world = world;
            _ownership = ownership;
            world._heldSystems.Value!.Push(system);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ownership, null) is { } ownership)
            {
                _world._heldSystems.Value!.Pop();
                ownership.Dispose();
            }
        }
    }

    private void AdvanceEffects(LiveSystemState state)
    {
        if (SqlConnections != 0 || state.Expiring)
        {
            return;
        }
        state.Expiring = true;
        try
        {
            long now = _clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            state.TimedModules.Expire(now);
            state.Heat.Advance(now);
            state.Nanites.Advance(now);
        }
        finally
        {
            state.Expiring = false;
        }
    }

    internal void ProjectSystems(TransactionParticipants? participants)
    {
        using var persistence = EnterPersistence("resident projection");
        foreach (var entry in ParticipantSystems(participants).Order().Select(_systems.Get))
        {
            using var context = UseSystem(entry.Id);
            using var ownership = entry.Enter("transaction projection");
            entry.State.Combat?.Flush(participants);
            entry.State.Heat.Flush(participants);
            entry.State.Tractors.Flush(participants);
        }
    }

    internal IDisposable EnterTransactionSystems(TransactionParticipants? participants) =>
        _systems.Enter(ParticipantSystems(participants), "transaction participants");

    private IReadOnlyCollection<int> ParticipantSystems(TransactionParticipants? participants)
    {
        if (participants is null)
        {
            return _systems.Snapshot().Select(entry => entry.Id).ToArray();
        }
        HashSet<int> systems = [.. participants.Systems];
        if (CurrentSystem is int current)
        {
            systems.Add(current);
        }
        if (participants.Pilots.Count + participants.Ships.Count + participants.Resources.Count == 0)
        {
            return systems;
        }
        foreach (long pilot in participants.Pilots)
        {
            if (_pilotSystems.TryGetValue(pilot, out int system))
            {
                systems.Add(system);
            }
        }
        foreach (long ship in participants.Ships)
        {
            if (_shipSystems.TryGetValue(ship, out int system))
            {
                systems.Add(system);
            }
        }
        var pilots = participants.Pilots.Where(pilot => !_pilotSystems.ContainsKey(pilot)).ToArray();
        var ships = participants.Ships.Where(ship => !_shipSystems.ContainsKey(ship)).ToArray();
        if (pilots.Length + ships.Length == 0 && participants.Resources.Count == 0)
        {
            return systems;
        }
        using WorldDbContext world = new(_path) { IsResidentProjection = true };
        var memberships = world
            .Ships.Where(ship => ships.Contains(ship.Id) || world.Pilots.Any(p => pilots.Contains(p.Id) && p.ActiveShipId == ship.Id))
            .Select(ship => new { ship.StationId, System = ship.Position == null ? null : ship.Position.SystemId })
            .ToArray();
        foreach (var membership in memberships)
        {
            systems.Add(membership.System ?? _database.Catalog.FindStation(membership.StationId)!.SolarSystemId);
        }
        var npcs = participants.Resources.Where(r => r.Kind == SharedSimulationResource.Npc).Select(r => r.Id).ToArray();
        if (npcs.Length != 0)
        {
            systems.UnionWith(world.Set<Eden.Data.World.Combat.Npcs.WorldNpc>().Where(n => npcs.Contains(n.Id)).Select(n => n.SystemId));
        }
        var wrecks = participants.Resources.Where(r => r.Kind == SharedSimulationResource.Wreck).Select(r => r.Id).ToArray();
        if (wrecks.Length != 0)
        {
            systems.UnionWith(world.Set<Eden.Data.World.Loot.WorldWreck>().Where(w => wrecks.Contains(w.Id)).Select(w => w.SystemId));
        }
        return systems;
    }

    internal void BeforeSystemsSql(string sql, bool readsResidentNpcTimer)
    {
        foreach (var entry in _systems.Snapshot())
        {
            using var context = UseSystem(entry.Id);
            using var ownership = entry.Enter("resident query projection");
            entry.State.Combat?.BeforeSql(sql, readsResidentNpcTimer);
            entry.State.Heat.BeforeSql(sql);
            if (sql.Contains("WorldWreck", StringComparison.Ordinal))
            {
                entry.State.Tractors.Flush();
            }
        }
    }

    internal void ClearSystems()
    {
        using var persistence = EnterPersistence("runtime reset");
        foreach (var entry in _systems.Snapshot())
        {
            using var context = UseSystem(entry.Id);
            using var ownership = entry.Enter("runtime reset");
            entry.State.Combat?.Clear();
            entry.State.Reads?.Clear();
            entry.State.Heat.InvalidateProfiles();
        }
    }

    internal CombatCheckpoint CaptureSystemCombat(CombatCheckpoint shared, TransactionParticipants? participants)
    {
        using var persistence = EnterPersistence("resident capture");
        List<CombatCheckpoint> captures = [shared];
        foreach (var entry in ParticipantSystems(participants).Order().Select(_systems.Get))
        {
            using var context = UseSystem(entry.Id);
            using var ownership = entry.Enter("resident capture");
            if (entry.State.Combat?.CaptureCheckpoint(participants) is { } capture)
            {
                captures.Add(capture);
            }
        }
        return new(
            captures.SelectMany(c => c.Ships).ToArray(),
            captures.SelectMany(c => c.Npcs).ToArray(),
            captures.SelectMany(c => c.Positions).ToArray(),
            captures.SelectMany(c => c.Drones).ToArray(),
            captures.SelectMany(c => c.Aggressions).ToArray(),
            captures.SelectMany(c => c.DroneTimers).ToArray(),
            captures.SelectMany(c => c.DroneEngagements).ToArray(),
            captures.SelectMany(c => c.DroneHostilities).ToArray(),
            captures.SelectMany(c => c.DroneTriggers).ToArray()
        );
    }

    private IEnumerable<LiveSystemState> AllStates => new[] { _shared }.Concat(_systems.Snapshot().Select(entry => entry.State));

    internal ShipHeatCheckpoint? CaptureSystemHeat()
    {
        using var persistence = EnterPersistence("heat capture");
        List<ShipHeatProjection> ships = [.. _shared.Heat.CaptureOwn()?.Ships ?? []];
        foreach (var entry in _systems.Snapshot())
        {
            using var context = UseSystem(entry.Id);
            using var ownership = entry.Enter("heat capture");
            entry.State.Heat.Advance(_clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            ships.AddRange(entry.State.Heat.CaptureOwn()?.Ships ?? []);
        }
        return ships.Count == 0 ? null : new(ships);
    }

    private void AdvanceSystemHeat()
    {
        foreach (var entry in _systems.Snapshot().Where(entry => entry.State.Heat.HasActive))
        {
            using var context = UseSystem(entry.Id);
            using var ownership = EnterSimulation("heat deadline", entry.Id, advanceEffects: false);
            entry.State.Heat.Advance(_clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        }
    }
}
