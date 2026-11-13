using System.Globalization;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Structures;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Market;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySequence(string Name, long Value);

internal sealed record EconomyStructure(long Id, int SystemId, int TypeId, Eden.Gameplay.Structures.StructureState State);

internal sealed record EconomyCheckpoint(IReadOnlyList<IEconomyProjection> Tables, IReadOnlyList<EconomySequence> Sequences)
{
    internal int Count => Tables.Sum(t => t.Count) + Sequences.Count;

    internal void Apply(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw("PRAGMA defer_foreign_keys=ON");
        foreach (var table in Tables)
        {
            table.Apply(world);
        }
        foreach (var sequence in Sequences)
        {
            world.Database.ExecuteSql($"UPDATE sqlite_sequence SET seq=MAX(seq,{sequence.Value}) WHERE name={sequence.Name}");
            world.Database.ExecuteSql(
                $"""
                INSERT INTO sqlite_sequence(name,seq) SELECT {sequence.Name},{sequence.Value}
                WHERE NOT EXISTS(SELECT 1 FROM sqlite_sequence WHERE name={sequence.Name})
                """
            );
        }
    }
}

// One authority shared by human commands and autonomous jobs. Published snapshots
// never escape as mutable entities; transaction views own their private materializations.
internal sealed partial class ResidentEconomy
{
    private const int ProjectionBatchSize = 128;
    private readonly LiveWorld _owner;
    private readonly WorldDatabase _database;
    private readonly Dictionary<Type, IEconomyTable> _tables = [];
    private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirtySequences = [];
    private readonly Dictionary<long, EconomyStructure> _structures = [];
    private readonly HashSet<long> _cloaked = [];

    internal EconomyStructure? Structure(long id) => _structures.GetValueOrDefault(id);

    internal bool Cloaked(long ship) => _cloaked.Contains(ship);

    private readonly Dictionary<int, InstitutionalPreparation> _prepared = [];
    private readonly SortedSet<(long Expires, long Id)> _openOrders = [];
    private readonly Dictionary<long, long> _marketHeads = [];
    private readonly HashSet<long> _staleHeads = [];
    internal long NextExpiry => _openOrders.Count == 0 ? long.MaxValue : _openOrders.Min.Expires;

    internal IReadOnlyCollection<EconomyKey> NpcMarketOrders(NpcMarketAssortment assortment) =>
        ((EconomyTable<NpcMarketOrder, EconomyNpcMarketOrder>)_tables[typeof(NpcMarketOrder)]).Indexed(
            nameof(NpcMarketAssortment),
            assortment
        );

    private void OrderChanged(EconomySavedPersonalOrder? before, EconomySavedPersonalOrder? after)
    {
        if (before is not null && after is not null && before.Status == after.Status && before.Expires == after.Expires)
        {
            return;
        }
        if (before is { Status: Eden.Gameplay.Market.PersonalOrderStatus.Open })
        {
            _openOrders.Remove((before.Expires, before.Id));
        }
        if (after is { Status: Eden.Gameplay.Market.PersonalOrderStatus.Open })
        {
            _openOrders.Add((after.Expires, after.Id));
        }
    }

    private void MarketChangeChanged(EconomySavedMarketChange? before, EconomySavedMarketChange? after)
    {
        if (
            before is not null
            && (after is null || after.PilotId != before.PilotId)
            && _marketHeads.GetValueOrDefault(before.PilotId) == before.Id
        )
        {
            _staleHeads.Add(before.PilotId);
        }
        if (after is not null)
        {
            _marketHeads[after.PilotId] = Math.Max(_marketHeads.GetValueOrDefault(after.PilotId), after.Id);
        }
    }

    internal long MarketHead(long pilot)
    {
        if (_staleHeads.Remove(pilot))
        {
            var table =
                (EconomyTable<Eden.Data.World.Market.SavedMarketChange, EconomySavedMarketChange>)
                    _tables[typeof(Eden.Data.World.Market.SavedMarketChange)];
            _marketHeads[pilot] = table.Matching(c => c.PilotId == pilot).Select(key => key.Id).DefaultIfEmpty().Max();
        }
        return _marketHeads.GetValueOrDefault(pilot);
    }

    internal int StructureSystemId(long id) =>
        _structures.TryGetValue(id, out var structure) ? structure.SystemId : throw new InvalidDataException("Unknown structure.");

    internal bool Prepared(InstitutionalPreparation value) => _prepared.GetValueOrDefault(value.StationId) == value;

    internal void Remember(IReadOnlyDictionary<int, InstitutionalPreparation> preparations)
    {
        foreach (var pair in preparations)
        {
            _prepared[pair.Key] = pair.Value;
        }
    }

    internal IReadOnlyDictionary<Type, IEconomyTable> Mappings => _tables;
    private bool _loaded;
    internal bool IsLoaded => _loaded;

    internal bool HasActiveAction => _transaction;

    internal void PublishCombat(Eden.World.Runtime.Combat.CombatCheckpoint checkpoint)
    {
        if (!checkpoint.HasChanges)
        {
            return;
        }
        using var action = Begin(new(systems: _owner.CombatSystems(checkpoint)));
        action.ImportCheckpoint(checkpoint);
        action.Commit();
    }

    internal void Warm()
    {
        using var action = Begin(TransactionParticipants.None);
        action.Commit();
    }

    private bool _projecting;
    internal bool IsProjecting => _projecting;

    internal T? Peek<T>(long id)
        where T : class => _tables.TryGetValue(typeof(T), out var table) && table is IEconomyPeek<T> typed ? typed.Peek(new(id)) : null;

    private bool _transaction;
    private EconomyTransaction? _active;

    internal T Inspect<T>(Func<WorldAction, T> read, TransactionParticipants? participants = null)
    {
        using var lease = _owner.EnterShared("resident inspection");
        if (_active is not null)
        {
            return read(_active);
        }
        using var action = Begin(participants ?? TransactionParticipants.None);
        return read(action);
    }

    internal T Read<T>(Func<WorldAction, T> read, TransactionParticipants? participants = null)
    {
        using var lease = _owner.EnterShared("resident economic read");
        if (_active is not null)
        {
            return read(_active);
        }
        using var action = Begin(participants ?? new TransactionParticipants());
        var result = read(action);
        action.Commit();
        return result;
    }

    internal ResidentEconomy(LiveWorld owner, WorldDatabase database)
    {
        _owner = owner;
        _database = database;
        RegisterTables();
    }

    private void Register<TRow, TState>(EconomyTable<TRow, TState> table)
        where TRow : class
        where TState : class => _tables.Add(typeof(TRow), table);

    internal EconomySet<T> Open<T>(EconomyTransaction transaction)
        where T : class =>
        _tables.TryGetValue(typeof(T), out var table) && table is IEconomyTable<T> typed
            ? typed.Open(transaction)
            : throw new InvalidOperationException($"{typeof(T).Name} has no resident economic mapping.");

    internal EconomyTransaction Begin(TransactionParticipants? participants = null)
    {
        var lease = _owner.EnterShared("economic transaction");
        IDisposable? systems = null;
        try
        {
            if (_transaction || _owner.SqlConnections != 0)
            {
                throw new InvalidOperationException("Resident economic transactions cannot overlap another transaction.");
            }
            EnsureLoaded();
            if (participants is not null)
            {
                participants = new(participants.Systems, participants.Pilots.Where(IsPilot), participants.Ships, participants.Resources);
                foreach (long pilot in participants.Pilots)
                {
                    TrackPilot(pilot);
                }
            }
            if (participants is not null)
            {
                participants = new(
                    _owner.ParticipantSystems(participants),
                    participants.Pilots,
                    participants.Ships,
                    participants.Resources
                );
            }
            systems = _owner.EnterTransactionSystems(participants);
            _ = _owner.CapsuleerActivity.Read();
            _transaction = true;
            _active = new(this, _owner, _database, lease, systems, participants);
            _active.ImportCombat(participants);
            return _active;
        }
        catch
        {
            _active = null;
            _transaction = false;
            systems?.Dispose();
            lease.Dispose();
            throw;
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }
        // The first image includes outstanding combat payments. This is hydration,
        // not a simulation path; subsequent economic actions use typed snapshots.
        _owner.ProjectSystems(null);
        _owner.ProjectShared(null);
        using WorldDbContext world = new(_database.Path) { IsResidentProjection = true };
        world.Database.OpenConnection();
        LoadProjectionDependencies(world);
        _structures.Clear();
        _cloaked.Clear();
        _sequences.Clear();
        _prepared.Clear();
        _openOrders.Clear();
        ClearTimedModules();
        ClearCommandBursts();
        ClearCombatIndexes();
        _marketHeads.Clear();
        _staleHeads.Clear();
        foreach (var table in _tables.Values)
        {
            table.Load(world);
        }
        foreach (var state in world.Set<Eden.Data.World.Propulsion.SpatialNavigationState>().Where(s => s.CloakModuleId != 0))
        {
            _cloaked.Add(state.ShipId);
        }
        foreach (var structure in world.Set<SavedStructure>().Select(s => new EconomyStructure(s.Id, s.SystemId, s.TypeId, s.State)))
        {
            _structures[structure.Id] = structure;
        }
        using var command = world.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name,seq FROM sqlite_sequence";
        SessionPerformance.DatabaseCommand(command.CommandText);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            _sequences[reader.GetString(0)] = reader.GetInt64(1);
        }
        ReindexLoadedTimedModules();
        _loaded = true;
    }

    internal bool ActiveShip(long pilot, long ship)
    {
        using var lease = _owner.EnterShared("resident ship membership");
        EnsureLoaded();
        return Peek<Eden.Data.World.Characters.Pilot>(pilot)?.ActiveShipId == ship;
    }

    internal long? ActivePilot(long ship)
    {
        var table = (EconomyTable<Eden.Data.World.Characters.Pilot, EconomyPilot>)_tables[typeof(Eden.Data.World.Characters.Pilot)];
        return table.Matching(p => p.ActiveShipId == ship).Select(key => (long?)key.Id).SingleOrDefault();
    }

    internal bool IsPilot(long id) => _pilotIdentities.ContainsKey(id);

    internal void TrackPilot(long id)
    {
        var pilots = (EconomyTable<Eden.Data.World.Characters.Pilot, EconomyPilot>)_tables[typeof(Eden.Data.World.Characters.Pilot)];
        var ships = (EconomyTable<Ship, EconomyShip>)_tables[typeof(Ship)];
        if (pilots.Snapshot(new(id)) is not { } pilot || ships.Snapshot(new(pilot.ActiveShipId)) is not { } ship)
        {
            return;
        }
        int system =
            Peek<Eden.Data.World.Docking.FlightPosition>(ship.Id)?.SystemId
            ?? (ship.StructureId is long structure ? _structures.GetValueOrDefault(structure)?.SystemId ?? 0 : 0);
        if (system == 0)
        {
            system = _database.Catalog.FindStation(ship.StationId)?.SolarSystemId ?? 0;
        }
        if (system != 0)
        {
            _owner.TrackShip(ship.Id, system, id);
        }
    }

    internal long Sequence(string name) => _sequences.GetValueOrDefault(name);

    internal void Committed(
        IReadOnlyDictionary<string, long> sequences,
        IReadOnlyCollection<CombatRowChange> changes,
        TransactionParticipants? participants,
        IReadOnlyList<long> pilots,
        IReadOnlyList<MarketLocation> marketLocations
    )
    {
        InvalidateFits(changes);
        ReindexTimedModules(changes);
        foreach (var sequence in sequences)
        {
            _sequences[sequence.Key] = sequence.Value;
            _dirtySequences.Add(sequence.Key);
        }
        if (changes.Count != 0 || sequences.Count != 0)
        {
            _owner.ObjectsChanged();
            foreach (long pilot in pilots)
            {
                TrackPilot(pilot);
                _owner.CombatForKnownPilot(pilot)?.ObserveEconomicItems(changes, id => Peek<OwnedItem>(id));
            }
            _owner.PublishEconomic(changes, participants, marketLocations);
        }
    }

    internal void End()
    {
        _active = null;
        _transaction = false;
    }

    internal bool ReferencesDirty(string sql)
    {
        if (!_loaded)
        {
            return false;
        }
        bool schema =
            sql.TrimStart().StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase)
            || sql.TrimStart().StartsWith("DROP ", StringComparison.OrdinalIgnoreCase)
            || sql.TrimStart().StartsWith("ALTER ", StringComparison.OrdinalIgnoreCase);
        return (schema || sql.Contains("sqlite_sequence", StringComparison.Ordinal))
                && (_dirtySequences.Count != 0 || _tables.Values.Any(table => table.HasChanges))
            || _tables.Values.Any(table => table.HasChanges && SqlTableReferences.Contains(sql, table.Name));
    }

    internal EconomyCheckpoint Capture() =>
        new(
            _tables.Values.Select(t => t.Capture()).Where(t => t.Count != 0).ToArray(),
            _dirtySequences.Select(name => new EconomySequence(name, _sequences[name])).ToArray()
        );

    internal void Flush() => FlushTables(_tables.Values.Select(table => table.Name));

    internal void FlushTables(IEnumerable<string> names)
    {
        if (!_loaded || _projecting)
        {
            return;
        }
        if (_transaction)
        {
            throw new InvalidOperationException("SQL cannot observe a speculative economic transaction.");
        }
        var selected = ProjectionTables(names, includeDependents: false);
        var projections = selected.Select(table => new EconomyTableProjection(table, table.Capture())).ToArray();
        var snapshot = ProjectionCheckpoint(projections, selected.Select(table => table.Name));
        if (snapshot.Count == 0)
        {
            return;
        }
        _projecting = true;
        try
        {
            using WorldDbContext world = new(_database.Path) { IsResidentProjection = true };
            world.Database.OpenConnection();
            using var transaction = world.Database.BeginTransaction();
            snapshot.Apply(world);
            transaction.Commit();
            foreach (var table in projections)
            {
                if (table.Rows.Count != 0)
                {
                    table.Table.Projected(world, table.Rows);
                }
            }
            foreach (var sequence in snapshot.Sequences)
            {
                _dirtySequences.Remove(sequence.Name);
            }
        }
        finally
        {
            _projecting = false;
        }
    }

    internal void Observe(IReadOnlyCollection<CombatRowChange> changes)
    {
        InvalidateFits(changes);
        if (!_loaded || _projecting)
        {
            return;
        }
        if (changes.Any(c => c.Table is "WorldMarketDefinition" or "WorldMarketMembership" or "WorldEconomy" or "schema"))
        {
            _prepared.Clear();
        }
        if (changes.Any(c => c.Table == "schema"))
        {
            if (Capture().Count != 0)
            {
                throw new InvalidOperationException("Project economic state before changing the world schema.");
            }
            _loaded = false;
            return;
        }
        var affected = changes.GroupBy(c => c.Table).ToDictionary(g => g.Key, g => g.Select(c => c.Id).Distinct().ToArray());
        var tables = _tables.Values.Where(t => affected.ContainsKey(t.Name)).ToArray();
        if (tables.Length == 0 && !affected.ContainsKey("WorldStructure") && !affected.ContainsKey("WorldSpatialNavigation"))
        {
            return;
        }
        using WorldDbContext world = new(_database.Path) { IsResidentProjection = true };
        world.Database.OpenConnection();
        foreach (var table in tables)
        {
            table.Observe(world, affected[table.Name]);
        }
        ReindexTimedModules(changes);
        if (affected.TryGetValue("WorldStructure", out var structures))
        {
            foreach (long id in structures)
            {
                _structures.Remove(id);
            }
            foreach (
                var structure in world
                    .Set<SavedStructure>()
                    .Where(s => structures.Contains(s.Id))
                    .Select(s => new EconomyStructure(s.Id, s.SystemId, s.TypeId, s.State))
            )
            {
                _structures[structure.Id] = structure;
            }
        }
        if (affected.TryGetValue("WorldSpatialNavigation", out var navigation))
        {
            foreach (long ship in navigation)
            {
                _cloaked.Remove(ship);
            }
            foreach (
                var state in world
                    .Set<Eden.Data.World.Propulsion.SpatialNavigationState>()
                    .Where(s => navigation.Contains(s.ShipId) && s.CloakModuleId != 0)
            )
            {
                _cloaked.Add(state.ShipId);
            }
        }
        // External insertions share the same high-water domain, including deletions
        // of the last row and item-to-ship/implant conversions.
        using var command = world.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name,seq FROM sqlite_sequence";
        SessionPerformance.DatabaseCommand(command.CommandText);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string name = reader.GetString(0);
            _sequences[name] = Math.Max(_sequences.GetValueOrDefault(name), reader.GetInt64(1));
        }
    }
}
