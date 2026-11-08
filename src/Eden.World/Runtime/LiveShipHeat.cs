using System.Collections.Frozen;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Thermal;
using Eden.World.Catalog;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime;

internal sealed record ShipHeatProjection(long ShipId, ShipHeatSnapshot State);

internal sealed record ShipHeatCheckpoint(IReadOnlyList<ShipHeatProjection> Ships)
{
    internal void Apply(WorldDbContext world)
    {
        using var transaction = world.Database.CurrentTransaction is null ? world.Database.BeginTransaction() : null;
        foreach (var ship in Ships)
        {
            foreach (var module in ship.State.Modules.Where(m => m.Condition.Damage > 0))
            {
                long id = module.ItemId;
                double damage = module.Condition.Damage;
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldModuleCondition(ModuleId,Damage) VALUES({id},{damage})
                    ON CONFLICT(ModuleId) DO UPDATE SET Damage=excluded.Damage
                    """
                );
            }
            string data = ShipHeatPersistence.Encode(ship.State);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldShipHeat(ShipId,StateJson) VALUES({ship.ShipId},{data})
                ON CONFLICT(ShipId) DO UPDATE SET StateJson=excluded.StateJson
                """
            );
        }
        transaction?.Commit();
    }
}

// Owns paid heat, deterministic damage trials and checkpoint projection.
// Burnout commits module state and activity removal at the funded deadline.
internal sealed class LiveShipHeat(LiveWorld owner, WorldDatabase database, TimeProvider clock)
{
    private const int RetainedIdleShips = 512;
    private readonly Dictionary<long, LoadedShipHeat> _ships = [];
    private readonly HashSet<long> _dirty = [];
    private readonly HashSet<long> _stale = [];
    private readonly HashSet<long> _reloadSaved = [];
    private readonly HashSet<long> _active = [];
    private int _internal;

    private readonly Dictionary<long, (long Pilot, long Ship)> _requested = [];

    internal bool Selected(long pilot, long module, long? ship = null)
    {
        using var ownership = owner.Enter();
        return _requested.TryGetValue(module, out var request) && request.Pilot == pilot && (ship is null || request.Ship == ship);
    }

    private readonly Dictionary<(long Pilot, long Ship), IReadOnlySet<long>> _requestViews = [];

    internal IReadOnlySet<long> Requested(long pilot, long ship)
    {
        using var ownership = owner.Enter();
        if (!_requestViews.TryGetValue((pilot, ship), out var view))
        {
            if (_requestViews.Count >= RetainedIdleShips)
            {
                _requestViews.Clear();
            }
            view = _requested.Where(p => p.Value == (pilot, ship)).Select(p => p.Key).ToFrozenSet();
            _requestViews[(pilot, ship)] = view;
        }
        return view;
    }

    internal void Select(long pilot, long ship, IReadOnlyList<long> modules, bool enabled)
    {
        using var ownership = owner.Enter();
        _requestViews.Remove((pilot, ship));
        foreach (long module in modules)
        {
            if (enabled)
            {
                _requested[module] = (pilot, ship);
            }
            else
            {
                _requested.Remove(module);
            }
        }
    }

    internal void ClearSelection(long pilot, long ship)
    {
        using var ownership = owner.Enter();
        _requestViews.Remove((pilot, ship));
        foreach (long module in _requested.Where(p => p.Value == (pilot, ship)).Select(p => p.Key).ToArray())
        {
            _requested.Remove(module);
        }
    }

    internal void Warm()
    {
        using var ownership = owner.Enter();
        _internal++;
        try
        {
            using WorldDbContext world = new(database.Path);
            long[] ships = world
                .Database.SqlQueryRaw<long>(
                    """
                    SELECT ShipId AS Value FROM WorldShipHeat
                    WHERE json_array_length(StateJson,'$.Cycles') > 0
                       OR EXISTS(
                         SELECT 1 FROM json_each(StateJson,'$.Racks')
                         WHERE json_extract(value,'$.Heat') > 0)
                    """
                )
                .ToArray();
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            foreach (long ship in ships)
            {
                if (Load(ship, now, false) is not { } loaded)
                {
                    continue;
                }
                _ships[ship] = loaded;
                if (!loaded.State.Idle)
                {
                    _active.Add(ship);
                }
                if (loaded.Changed)
                {
                    _dirty.Add(ship);
                    owner.ObjectsChanged();
                }
            }
        }
        finally
        {
            _internal--;
        }
    }

    internal void PublishCommitted(long pilot, long ship, ShipHeatSnapshot state)
    {
        _ships[ship] = new(pilot, ship, state, HasSavedState: true, Changed: false);
        _dirty.Remove(ship);
        _stale.Remove(ship);
        _reloadSaved.Remove(ship);
        if (state.Idle)
        {
            _active.Remove(ship);
        }
        else
        {
            _active.Add(ship);
        }
        Trim(ship);
    }

    internal void PublishResident(long ship, ShipHeatSnapshot state)
    {
        _ships[ship] = _ships[ship] with { State = state };
        _active.Add(ship);
        _dirty.Add(ship);
        owner.ObjectsChanged();
    }

    internal ShipHeatSnapshot Read(long pilot, long ship, long now)
    {
        using var ownership = owner.Enter();
        if (!_ships.TryGetValue(ship, out var loaded))
        {
            loaded = Load(ship, now, false, expectedPilot: pilot) ?? throw new InvalidDataException("Read heat only on an owned ship.");
            _ships[ship] = loaded;
            if (!loaded.State.Idle)
            {
                _active.Add(ship);
            }
            if (loaded.Changed)
            {
                _dirty.Add(ship);
                owner.ObjectsChanged();
            }
            Trim(ship);
        }
        if (_stale.Contains(ship))
        {
            _active.Add(ship);
        }
        Advance(now);
        if (!_ships.TryGetValue(ship, out loaded) || loaded.PilotId != pilot)
        {
            throw new InvalidDataException("Read heat only on an owned ship.");
        }
        return loaded.State;
    }

    internal ModuleCondition? Condition(long pilot, long ship, long module) =>
        _internal == 0 && !_stale.Contains(ship) && _ships.TryGetValue(ship, out var loaded) && loaded.PilotId == pilot
            ? loaded.State.Modules.FirstOrDefault(m => m.ItemId == module)?.Condition
            : null;

    private sealed record ConditionView(
        IReadOnlyList<HeatDamageModule> Modules,
        IReadOnlyList<FittedModuleCondition> Baseline,
        IReadOnlyList<FittedModuleCondition> Result
    );

    private readonly Dictionary<long, ConditionView> _conditionViews = [];

    internal IReadOnlyList<FittedModuleCondition> Conditions(long ship, IReadOnlyList<FittedModuleCondition> baseline)
    {
        if (_stale.Contains(ship) || !_ships.TryGetValue(ship, out var loaded))
        {
            return baseline;
        }
        if (
            _conditionViews.TryGetValue(ship, out var cached)
            && ReferenceEquals(cached.Modules, loaded.State.Modules)
            && ReferenceEquals(cached.Baseline, baseline)
        )
        {
            return cached.Result;
        }
        var modules = loaded.State.Modules.ToDictionary(m => m.ItemId);
        var result = Array.AsReadOnly(
            baseline
                .Select(m =>
                    modules.TryGetValue(m.Condition.ItemId, out var live)
                        ? m with
                        {
                            Condition = live.Condition,
                            Online = live.Online,
                            BurnedAt = live.BurnedAt,
                        }
                        : m
                )
                .ToArray()
        );
        _conditionViews[ship] = new(loaded.State.Modules, baseline, result);
        return result;
    }

    internal void InvalidateProfiles()
    {
        Flush();
        foreach (long ship in _ships.Keys)
        {
            _stale.Add(ship);
        }
    }

    internal void Observe(IReadOnlyCollection<CombatRowChange> changes)
    {
        if (_internal != 0 || _ships.Count == 0 || changes.Count == 0)
        {
            return;
        }
        bool positionMembership = changes.Any(c => c.Table == "WorldPositionMembership");
        if (
            !changes.Any(c =>
                c.Table
                    is "schema"
                        or "WorldShip"
                        or "WorldShipHeat"
                        or "WorldFitRevision"
                        or "WorldModuleCondition"
                        or "WorldRemoteAssistance"
                || positionMembership && c.Table == "WorldPosition"
            )
        )
        {
            return;
        }
        foreach (var loaded in _ships.Values)
        {
            if (changes.Any(c => c.Table == "WorldShipHeat" && c.Id == loaded.ShipId))
            {
                _reloadSaved.Add(loaded.ShipId);
                _active.Add(loaded.ShipId);
            }
            if (
                changes.Any(c =>
                    c.Table == "schema"
                    || c.Table is "WorldShip" or "WorldShipHeat" && c.Id == loaded.ShipId
                    || c.Table == "WorldFitRevision" && c.Id == loaded.PilotId
                    || positionMembership && c.Table == "WorldPosition" && c.Id == loaded.ShipId
                    || c.Table == "WorldModuleCondition" && loaded.State.Modules.Any(s => s.ItemId == c.Id)
                    || c.Table == "WorldRemoteAssistance" && loaded.State.Modules.Any(s => s.ItemId == c.Id)
                )
            )
            {
                _stale.Add(loaded.ShipId);
            }
        }
    }

    internal void Advance(long now)
    {
        if (_internal != 0)
        {
            return;
        }
        _internal++;
        try
        {
            foreach (long ship in _active.ToArray())
            {
                var previous = _ships[ship];
                var loaded = _stale.Contains(ship) ? Load(ship, now, true, _reloadSaved.Contains(ship) ? null : previous.State) : previous;
                if (loaded is null)
                {
                    _stale.Remove(ship);
                    _reloadSaved.Remove(ship);
                    _conditionViews.Remove(ship);
                    _ships.Remove(ship);
                    _dirty.Remove(ship);
                    _active.Remove(ship);
                    continue;
                }
                var next = loaded.State.Idle ? loaded.State : HeatDamageRules.Advance(loaded.State, now);
                var burned = next
                    .Modules.Where(m =>
                        m.Condition.BurnedOut
                        && (m.Online || loaded.State.Modules.Any(old => old.ItemId == m.ItemId && !old.Condition.BurnedOut))
                    )
                    .OrderBy(m => m.BurnedAt)
                    .ThenBy(m => m.ItemId)
                    .ToArray();
                if (burned.Length > 0)
                {
                    using WorldDbContext world = new(database.Path);
                    using var transaction = world.Database.BeginTransaction();
                    using CatalogDbContext catalog = new(database.CatalogPath);
                    new ShipHeatCheckpoint([new(ship, next)]).Apply(world);
                    foreach (var module in burned)
                    {
                        ModuleConditionPersistence.ApplyDamage(world, catalog, module.ItemId, 0, module.BurnedAt ?? next.UpdatedAt);
                    }
                    world.SaveChanges();
                    transaction.Commit();
                    foreach (var module in burned)
                    {
                        _requested.Remove(module.ItemId);
                        _requestViews.Remove((loaded.PilotId, ship));
                    }
                }
                _stale.Remove(ship);
                _reloadSaved.Remove(ship);
                if (next.Idle)
                {
                    _active.Remove(ship);
                }
                if (!ReferenceEquals(previous, loaded) || !ReferenceEquals(previous.State, next))
                {
                    _ships[ship] = loaded with { State = next };
                    if (
                        !HeatCycleRules.SameState(previous.State, next)
                        && !(previous.State.Idle && next.Idle)
                        && (loaded.HasSavedState || previous.HasSavedState || !previous.State.Idle || !next.Idle)
                    )
                    {
                        _dirty.Add(ship);
                        owner.ObjectsChanged();
                    }
                }
            }
        }
        finally
        {
            _internal--;
        }
    }

    private LoadedShipHeat? Load(long ship, long now, bool liveChange, ShipHeatSnapshot? current = null, long? expectedPilot = null)
    {
        _internal++;
        try
        {
            using WorldDbContext world = new(database.Path);
            using var transaction = world.Database.BeginTransaction();
            using CatalogDbContext catalog = new(database.CatalogPath);
            var loaded = ShipHeatPersistence.Load(world, catalog, ship, now, liveChange, current, expectedPilot);
            transaction.Commit();
            return loaded;
        }
        finally
        {
            _internal--;
        }
    }

    internal ShipHeatCheckpoint? Capture() =>
        _dirty.Count == 0
            ? null
            : new(_dirty.Where(_ships.ContainsKey).Select(id => new ShipHeatProjection(id, _ships[id].State)).ToArray());

    internal void BeforeSql(string sql)
    {
        if (
            _internal == 0
            && _dirty.Count > 0
            && (
                !sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                || sql.Contains("WorldShipHeat", StringComparison.Ordinal)
            )
        )
        {
            Flush();
        }
    }

    internal void Flush()
    {
        if (_internal != 0)
        {
            return;
        }
        Advance(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        if (_dirty.Count == 0)
        {
            return;
        }
        _internal++;
        try
        {
            using WorldDbContext world = new(database.Path);
            Capture()!.Apply(world);
            _dirty.Clear();
        }
        finally
        {
            _internal--;
        }
    }

    private void Trim(long protectedShip)
    {
        int excess = _ships.Count(p => p.Value.State.Idle && !_dirty.Contains(p.Key)) - RetainedIdleShips;
        foreach (
            long ship in _ships
                .Where(p => p.Key != protectedShip && p.Value.State.Idle && !_dirty.Contains(p.Key))
                .Select(p => p.Key)
                .Take(Math.Max(0, excess))
                .ToArray()
        )
        {
            _conditionViews.Remove(ship);
            _ships.Remove(ship);
            _stale.Remove(ship);
            _reloadSaved.Remove(ship);
            _active.Remove(ship);
        }
    }
}
