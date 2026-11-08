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

// Temperature/contribution ownership only. The forthcoming damage consumer must
// settle completions before any native overload command can be enabled.
internal sealed class LiveShipHeat(LiveWorld owner, WorldDatabase database, TimeProvider clock)
{
    private const int RetainedIdleShips = 512;
    private readonly Dictionary<long, LoadedShipHeat> _ships = [];
    private readonly HashSet<long> _dirty = [];
    private readonly HashSet<long> _stale = [];
    private readonly HashSet<long> _reloadSaved = [];
    private readonly HashSet<long> _active = [];
    private int _internal;

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

    internal void InvalidateProfiles()
    {
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
                    || c.Table == "WorldModuleCondition" && loaded.State.Cycles.Any(s => s.ModuleId == c.Id)
                    || c.Table == "WorldRemoteAssistance" && loaded.State.Cycles.Any(s => s.ModuleId == c.Id)
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
                _stale.Remove(ship);
                _reloadSaved.Remove(ship);
                if (loaded is null)
                {
                    _ships.Remove(ship);
                    _dirty.Remove(ship);
                    _active.Remove(ship);
                    continue;
                }
                var next = loaded.State.Idle ? loaded.State : HeatCycleRules.Advance(loaded.State, now).State;
                if (next.Idle)
                {
                    _active.Remove(ship);
                }
                if (!ReferenceEquals(previous, loaded) || !ReferenceEquals(previous.State, next))
                {
                    _ships[ship] = loaded with { State = next };
                    if (
                        !HeatCycleRules.SameState(previous.State, next)
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
            _ships.Remove(ship);
            _stale.Remove(ship);
            _reloadSaved.Remove(ship);
            _active.Remove(ship);
        }
    }
}
