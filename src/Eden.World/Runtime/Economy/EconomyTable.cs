using System.Collections;
using System.Data.Common;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Text.Json;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal readonly record struct EconomyKey(long Id, long Part = 0, string? Name = null);

internal sealed record EconomyChange<T>(EconomyKey Key, T? Value)
    where T : class;

internal interface IEconomyProjection
{
    int Count { get; }
    void Apply(WorldDbContext world);
}

internal sealed record EconomyProjection<T>(
    IReadOnlyList<EconomyChange<T>> Rows,
    Action<WorldDbContext, IReadOnlyList<EconomyChange<T>>> Project
) : IEconomyProjection
    where T : class
{
    public int Count => Rows.Count;

    public void Apply(WorldDbContext world) => Project(world, Rows);
}

internal interface IEconomySet
{
    int Materializations { get; }
    bool Prepare();
    void Publish();
    IReadOnlyCollection<Eden.World.Runtime.Combat.CombatRowChange> Changes { get; }
    IReadOnlyCollection<long> Owners { get; }
}

internal interface IEconomyPeek<TRow>
    where TRow : class
{
    TRow? Peek(EconomyKey key);
}

internal interface IEconomyTable<TRow> : IEconomyTable, IEconomyPeek<TRow>
    where TRow : class
{
    EconomySet<TRow> Open(EconomyTransaction transaction);
}

internal interface IEconomyTable
{
    string Name { get; }
    IReadOnlyList<string> Columns { get; }
    bool HasChanges { get; }
    bool Contains(EconomyKey key);
    void Load(WorldDbContext world);
    IEconomyProjection Capture();
    void Projected(WorldDbContext world, IEconomyProjection projection);
    void Observe(WorldDbContext world, IReadOnlyList<long> rowIds);
}

internal interface IEconomyRows<TState>
    where TState : class
{
    IEnumerable<EconomyKey> Keys { get; }
    TState? Read(EconomyKey key);
    IReadOnlyCollection<EconomyKey>? Candidates(string column, object? value);
    void Set(EconomyKey key, TState? row);
    void Clear();
}

internal sealed class EconomyTable<TRow, TState>(
    string name,
    Func<TRow, EconomyKey> key,
    Func<TRow, TState> capture,
    Func<TState, TRow> materialize,
    Action<WorldDbContext, IReadOnlyList<EconomyChange<TState>>> project,
    string columns,
    Func<DbDataReader, TState> read,
    Func<TState, EconomyKey> stateKey,
    bool readOnly = false,
    Func<TState, TState, bool>? equal = null,
    IReadOnlyList<EconomyIndex<TState>>? indexes = null,
    Action<TState?, TState?>? changed = null,
    string? primaryColumn = "Id",
    bool rowIdIsKey = false,
    IEconomyRows<TState>? groupedRows = null
) : IEconomyTable<TRow>
    where TRow : class
    where TState : class
{
    private readonly Dictionary<EconomyKey, TState>? _rows = rowIdIsKey || groupedRows is not null ? null : [];
    private readonly Dictionary<long, TState>? _integerRows = rowIdIsKey ? [] : null;

    // Keep the SQLite baseline only until a resident change is projected or reverted.
    private readonly Dictionary<EconomyKey, TState?> _sqlBaselines = [];

    // INTEGER PRIMARY KEY tables can use SQLite's update-hook row ID directly.
    private readonly Dictionary<long, EconomyKey>? _rowIds = rowIdIsKey ? null : [];
    private readonly Dictionary<string, EconomyIndex<TState>> _indexes = (indexes ?? []).ToDictionary(i => i.Name);

    internal IEnumerable<EconomyKey> Matching(Expression<Func<TRow, bool>> predicate)
    {
        var candidates = EconomyCandidates.Find(predicate.Body, predicate.Parameters[0], (column, value) => Candidates(column, value));
        var state = Expression.Parameter(typeof(TState), "saved");
        Func<TState, bool>? match = null;
        try
        {
            var expression = new EconomyPredicate(predicate.Parameters[0], state).Visit(predicate.Body);
            match = Expression.Lambda<Func<TState, bool>>(expression, state).Compile(preferInterpretation: true);
        }
        catch (ArgumentException)
        {
            // Navigation-dependent predicates use linked private rows instead.
        }
        catch (InvalidOperationException)
        {
            // A whole entity argument cannot be evaluated on its scalar snapshot.
        }
        var keys = candidates ?? Keys;
        return keys.Where(id => Snapshot(id) is { } row && (match is null || match(row))).ToArray();
    }

    private IReadOnlyCollection<EconomyKey>? Candidates(string column, object? value)
    {
        if (column == primaryColumn)
        {
            long? id =
                value is long wide ? wide
                : value is int narrow ? narrow
                : null;
            return id is long known && Contains(new(known)) ? [new(known)] : [];
        }
        return _indexes.TryGetValue(column, out var index) ? index.Find(value) : groupedRows?.Candidates(column, value);
    }

    private void Replace(EconomyKey id, TState? state, TState? before)
    {
        if (groupedRows is not null)
        {
            groupedRows.Set(id, state);
        }
        else if (state is null)
        {
            if (_integerRows is not null)
            {
                _integerRows.Remove(id.Id);
            }
            else
            {
                _rows!.Remove(id);
            }
        }
        else
        {
            if (_integerRows is not null)
            {
                _integerRows[id.Id] = state;
            }
            else
            {
                _rows![id] = state;
            }
        }
        foreach (var index in _indexes.Values)
        {
            if (state is null)
            {
                if (before is not null)
                {
                    index.Remove(id, before);
                }
            }
            else if (before is null)
            {
                index.Add(id, state);
            }
            else
            {
                index.Replace(id, before, state);
            }
        }
        changed?.Invoke(before, state);
    }

    private void ReplaceFromSql(EconomyKey id, TState? state) =>
        Replace(id, state, _indexes.Count != 0 || changed is not null ? Snapshot(id) : null);

    internal string Name => name;
    string IEconomyTable.Name => name;
    public bool HasChanges => _sqlBaselines.Count != 0;
    public IReadOnlyList<string> Columns => columns.Split(',');
    internal IEnumerable<EconomyKey> Keys =>
        groupedRows?.Keys ?? (_integerRows is not null ? _integerRows.Keys.Select(static id => new EconomyKey(id)) : _rows!.Keys);

    internal IReadOnlyCollection<EconomyKey> Indexed(string column, object? value) => _indexes[column].Find(value);

    internal EconomyKey Key(TRow row) => key(row);

    internal TState Capture(TRow row) => capture(row);

    public TRow? Peek(EconomyKey id) => Read(id);

    public bool Contains(EconomyKey id) => Snapshot(id) is not null;

    internal TState? Snapshot(EconomyKey id)
    {
        if (groupedRows is not null)
        {
            return groupedRows.Read(id);
        }
        if (_integerRows is null)
        {
            return _rows!.TryGetValue(id, out var row) ? row : null;
        }
        return id is { Part: 0, Name: null } && _integerRows.TryGetValue(id.Id, out var integerRow) ? integerRow : null;
    }

    internal TRow? Read(EconomyKey id) => Snapshot(id) is { } state ? materialize(state) : null;

    internal bool Same(TState? first, TState? second) =>
        first is null
            ? second is null
            : second is not null && (equal?.Invoke(first, second) ?? EqualityComparer<TState>.Default.Equals(first, second));

    internal bool Same(EconomyKey id, TState? state) => Same(Snapshot(id), state);

    internal void Validate(EconomyKey id, TState? state)
    {
        if (readOnly && !Same(id, state))
        {
            throw new InvalidOperationException($"Resident economy cannot mutate reference table {name}.");
        }
    }

    internal void Publish(EconomyKey id, TState? state)
    {
        var before = Snapshot(id);
        Replace(id, state, before);
        bool dirty = _sqlBaselines.TryGetValue(id, out var saved);
        if (Same(dirty ? saved : before, state))
        {
            _sqlBaselines.Remove(id);
        }
        else if (!dirty)
        {
            _sqlBaselines.Add(id, before);
        }
    }

    public void Load(WorldDbContext world)
    {
        groupedRows?.Clear();
        _rows?.Clear();
        _integerRows?.Clear();
        foreach (var index in _indexes.Values)
        {
            index.Clear();
        }
        _sqlBaselines.Clear();
        _rowIds?.Clear();
        ReadRows(world, null);
    }

    private void ReadRows(WorldDbContext world, IReadOnlyList<long>? rowIds)
    {
        using var command = world.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT rowid,{columns} FROM {name}";
        if (rowIds is not null)
        {
            command.CommandText += " WHERE rowid IN (SELECT value FROM json_each($ids))";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$ids";
            parameter.Value = JsonSerializer.Serialize(rowIds);
            command.Parameters.Add(parameter);
        }
        SessionPerformance.DatabaseCommand(command.CommandText);
        long started = Stopwatch.GetTimestamp();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var state = read(reader);
            var id = stateKey(state);
            if (_rowIds is not null)
            {
                _rowIds[reader.GetInt64(0)] = id;
            }
            ReplaceFromSql(id, state);
            if (_sqlBaselines.Count != 0)
            {
                _sqlBaselines.Remove(id);
            }
        }
        SessionPerformance.DatabaseElapsed(Stopwatch.GetElapsedTime(started));
    }

    public void Observe(WorldDbContext world, IReadOnlyList<long> rowIds)
    {
        foreach (long rowId in rowIds)
        {
            EconomyKey id = new(rowId);
            if (_rowIds is null || _rowIds.Remove(rowId, out id))
            {
                ReplaceFromSql(id, null);
                _sqlBaselines.Remove(id);
            }
        }
        ReadRows(world, rowIds);
    }

    public IEconomyProjection Capture() =>
        new EconomyProjection<TState>(_sqlBaselines.Keys.Select(id => new EconomyChange<TState>(id, Snapshot(id))).ToArray(), project);

    public void Projected(WorldDbContext world, IEconomyProjection projection)
    {
        var rows = ((EconomyProjection<TState>)projection).Rows;
        foreach (var change in rows)
        {
            if (_rowIds is not null)
            {
                if (change.Value is null)
                {
                    _rowIds.Remove(change.Key.Id);
                }
                else
                {
                    _rowIds[change.Key.Id] = change.Key;
                }
            }
            if (Same(change.Key, change.Value))
            {
                _sqlBaselines.Remove(change.Key);
            }
            else
            {
                _sqlBaselines[change.Key] = change.Value;
            }
        }
        if (_rowIds is not null && rows.Any(c => c.Key.Name is not null || c.Key.Part != 0))
        {
            _rowIds.Clear();
            ReadRows(world, null);
        }
    }

    public EconomySet<TRow> Open(EconomyTransaction transaction) => new EconomySet<TRow, TState>(this, transaction);
}

internal sealed record EconomyEntry<T>(T Entity, EntityState State = EntityState.Unchanged)
    where T : class;

internal abstract class EconomySet<T> : IQueryable<T>, IEconomySet
    where T : class
{
    protected abstract IEnumerable<T> Rows { get; }
    private IQueryProvider? _provider;
    public Type ElementType => typeof(T);
    public Expression Expression => Expression.Constant(this);
    public IQueryProvider Provider => _provider ??= new EconomyQueryProvider<T>(this);

    public IEnumerator<T> GetEnumerator() => Rows.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal abstract IEnumerable<T> SelectRows(Expression<Func<T, bool>>? predicate);

    internal virtual IEnumerable<TResult> Project<TResult>(Expression<Func<T, TResult>> selector, Expression<Func<T, bool>>? predicate) =>
        SelectRows(predicate).Select(selector.Compile(preferInterpretation: true));

    internal abstract T? Find(EconomyKey id);
    internal abstract IQueryable<T> Where(Expression<Func<T, bool>> predicate);

    internal bool Any(Expression<Func<T, bool>> predicate) => Where(predicate).Any();

    internal int Count(Expression<Func<T, bool>> predicate) => Where(predicate).Count();

    internal T Single(Expression<Func<T, bool>> predicate) => Where(predicate).Single();

    internal T? SingleOrDefault(Expression<Func<T, bool>> predicate) => Where(predicate).SingleOrDefault();

    internal T? FirstOrDefault(Expression<Func<T, bool>> predicate) => Where(predicate).FirstOrDefault();

    internal EconomySet<T> AsNoTracking() => this;

    internal EconomySet<T> AsTracking() => this;

    internal EconomySet<T> Include<TProperty>(Expression<Func<T, TProperty>> navigation) => this;

    internal T? Find(long id) => Find(new EconomyKey(id));

    internal T? Find(long id, long part) => Find(new EconomyKey(id, part));

    internal T? Find(long id, string name) => Find(new EconomyKey(id, Name: name));

    internal abstract IReadOnlyCollection<T> Local { get; }
    internal abstract EconomyEntry<T> Add(T row);
    internal abstract void Remove(T row);
    internal abstract EntityState State(T row);
    public int Materializations => Local.Count;
    public abstract bool Prepare();
    public abstract void Publish();
    public abstract IReadOnlyCollection<Eden.World.Runtime.Combat.CombatRowChange> Changes { get; }
    public abstract IReadOnlyCollection<long> Owners { get; }
}

internal sealed partial class EconomySet<TRow, TState>(EconomyTable<TRow, TState> table, EconomyTransaction transaction) : EconomySet<TRow>
    where TRow : class
    where TState : class
{
    private readonly Dictionary<EconomyKey, TRow> _local = [];
    private readonly HashSet<EconomyKey> _removed = [];
    private readonly Dictionary<EconomyKey, TState?> _changes = [];
    public override IReadOnlyCollection<Eden.World.Runtime.Combat.CombatRowChange> Changes
    {
        get
        {
            List<Eden.World.Runtime.Combat.CombatRowChange> changes = [];
            foreach (var key in _changes.Keys)
            {
                changes.Add(new(table.Name, key.Id, Resident: true));
                changes.AddRange(transaction.FitChanges(table.Read(key), _removed.Contains(key) ? null : _local[key]));
                if (typeof(TRow) == typeof(Eden.Data.World.Docking.FlightPosition))
                {
                    var before = table.Read(key) as Eden.Data.World.Docking.FlightPosition;
                    var after = _removed.Contains(key) ? null : _local[key] as Eden.Data.World.Docking.FlightPosition;
                    if ((before is null) != (after is null) || before?.SystemId != after?.SystemId)
                    {
                        changes.Add(new("WorldPositionMembership", key.Id, Resident: true));
                    }
                }
                if (typeof(TRow) == typeof(Eden.Data.World.Inventory.OwnedItem))
                {
                    var before = table.Read(key) as Eden.Data.World.Inventory.OwnedItem;
                    var after = _removed.Contains(key) ? null : _local[key] as Eden.Data.World.Inventory.OwnedItem;
                    long priorHold = OreHoldShip(before);
                    long nextHold = OreHoldShip(after);
                    if (priorHold > 0)
                    {
                        changes.Add(new("ShipOreHoldContents", priorHold, Resident: true));
                    }
                    if (nextHold > 0 && nextHold != priorHold)
                    {
                        changes.Add(new("ShipOreHoldContents", nextHold, Resident: true));
                    }
                    if (
                        before is null
                        || after is null
                        || before.OwnerId != after.OwnerId
                        || before.LocationId != after.LocationId
                        || before.FlagId != after.FlagId
                        || before.TypeId != after.TypeId
                        || before.Singleton != after.Singleton
                        || before.Online != after.Online
                    )
                    {
                        changes.Add(new("WorldItemMembership", key.Id, Resident: true));
                    }
                }
            }
            return changes;
        }
    }

    private static long OreHoldShip(Eden.Data.World.Inventory.OwnedItem? item) =>
        item is { FlagId: (int)Eden.Gameplay.Inventory.InventoryFlag.Cargo or (int)Eden.Gameplay.Inventory.InventoryFlag.GeneralMiningHold }
            ? item.LocationId
            : 0;

    public override IReadOnlyCollection<long> Owners =>
        _changes
            .Keys.SelectMany(key => new[] { table.Read(key), _removed.Contains(key) ? null : _local[key] })
            .OfType<TRow>()
            .Select(EconomyOwnership.Pilot)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
    internal override IReadOnlyCollection<TRow> Local => _local.Values;

    internal IEnumerable<TState> IndexedSnapshots(string column, int value, (string Column, int Value)? alternate = null)
    {
        var keys = table.Indexed(column, value);
        if (alternate is { } index)
        {
            var candidates = table.Indexed(index.Column, index.Value);
            if (candidates.Count < keys.Count)
            {
                keys = candidates;
            }
        }
        foreach (var key in keys)
        {
            if (!_local.ContainsKey(key) && table.Snapshot(key) is { } saved)
            {
                yield return saved;
            }
        }
        foreach (var (key, row) in _local)
        {
            if (!_removed.Contains(key))
            {
                yield return table.Capture(row);
            }
        }
    }

    internal override TRow? Find(EconomyKey id)
    {
        if (_removed.Contains(id))
        {
            return null;
        }
        if (!_local.TryGetValue(id, out var row))
        {
            row = table.Read(id);
            if (row is null)
            {
                return null;
            }
            if (!transaction.Overlay(row))
            {
                return null;
            }
            _local.Add(id, row);
            transaction.Link(row);
        }
        return transaction.Visible(row) ? row : null;
    }

    internal override IQueryable<TRow> Where(Expression<Func<TRow, bool>> predicate) => Queryable.Where(this, predicate);

    internal override IEnumerable<TRow> SelectRows(Expression<Func<TRow, bool>>? predicate)
    {
        var matches = predicate?.Compile(preferInterpretation: true);
        var candidates = predicate is null ? table.Keys : table.Matching(predicate);
        return candidates
            .Concat(_local.Keys)
            .Distinct()
            .ToArray()
            .Select(Find)
            .OfType<TRow>()
            .Where(row => matches is null || matches(row));
    }

    protected override IEnumerable<TRow> Rows => SelectRows(null);

    internal override EconomyEntry<TRow> Add(TRow row)
    {
        transaction.AssignIdentity(row);
        var id = table.Key(row);
        if (!_removed.Contains(id) && (_local.ContainsKey(id) || table.Contains(id)))
        {
            throw new InvalidDataException($"Duplicate resident {table.Name} identity.");
        }
        _removed.Remove(id);
        _local[id] = row;
        transaction.Link(row);
        return new(row, EntityState.Added);
    }

    internal override void Remove(TRow row)
    {
        var id = table.Key(row);
        _local[id] = row;
        if (_removed.Add(id))
        {
            transaction.RemoveDependents(row);
        }
    }

    internal override EntityState State(TRow row) => _removed.Contains(table.Key(row)) ? EntityState.Deleted : EntityState.Unchanged;

    public override bool Prepare()
    {
        _changes.Clear();
        foreach (var pair in _local)
        {
            if (table.Key(pair.Value) != pair.Key)
            {
                throw new InvalidDataException("An economic transaction cannot change an entity's identity.");
            }
            var next = _removed.Contains(pair.Key) ? null : table.Capture(pair.Value);
            table.Validate(pair.Key, next);
            if (!table.Same(pair.Key, next))
            {
                _changes.Add(pair.Key, next);
            }
        }
        return _changes.Count != 0;
    }

    public override void Publish()
    {
        foreach (var change in _changes)
        {
            table.Publish(change.Key, change.Value);
        }
    }
}
