using System.Collections.Frozen;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Thermal;
using Eden.World.Catalog;
using Eden.World.Runtime.Combat;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime;

internal sealed record ShipHeatProjection(long ShipId, ShipHeatSnapshot State);

internal sealed record ShipHeatCheckpoint(IReadOnlyList<ShipHeatProjection> Ships)
{
    internal void Stage(WorldAction world)
    {
        foreach (var ship in Ships)
        {
            foreach (var module in ship.State.Modules.Where(m => m.Condition.Damage > 0))
            {
                var condition =
                    world.Set<SavedModuleCondition>().Find(module.ItemId)
                    ?? world.Add(new SavedModuleCondition { ModuleId = module.ItemId }).Entity;
                condition.Damage = module.Condition.Damage;
            }
            var saved = world.Set<SavedShipHeat>().Find(ship.ShipId) ?? world.Add(new SavedShipHeat { ShipId = ship.ShipId }).Entity;
            saved.StateJson = ShipHeatPersistence.Encode(ship.State);
        }
    }

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
    private readonly ResidentMap<long, LoadedShipHeat> _ships = [];
    private readonly ResidentSet<long> _dirty = [];
    private readonly ResidentSet<long> _stale = [];
    private readonly ResidentSet<long> _reloadSaved = [];
    private readonly ResidentSet<long> _active = [];
    internal bool HasActive => _active.Count != 0;
    private int _internal;

    private readonly ResidentMap<long, (long Pilot, long Ship)> _requested = [];

    internal bool Selected(long pilot, long module, long? ship = null)
    {
        return owner.Run(() => SelectedOwned(pilot, module, ship), nameof(Selected));
    }

    private bool SelectedOwned(long pilot, long module, long? ship = null)
    {
        using var ownership = owner.Enter();
        return _requested.TryGetValue(module, out var request) && request.Pilot == pilot && (ship is null || request.Ship == ship);
    }

    private readonly ResidentMap<(long Pilot, long Ship), IReadOnlySet<long>> _requestViews = [];

    internal IReadOnlySet<long> Requested(long pilot, long ship)
    {
        return owner.Run(() => RequestedOwned(pilot, ship), nameof(Requested));
    }

    private IReadOnlySet<long> RequestedOwned(long pilot, long ship)
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
        owner.Run(
            () =>
            {
                var actual = owner.HeatForKnownShip(ship);
                if (!ReferenceEquals(actual, this))
                {
                    actual.Select(pilot, ship, modules, enabled);
                }
                else
                {
                    SelectOwned(pilot, ship, modules, enabled);
                }
            },
            nameof(Select)
        );
    }

    private void SelectOwned(long pilot, long ship, IReadOnlyList<long> modules, bool enabled)
    {
        using var ownership = owner.Enter();
        owner.OwnHeat(this, ship, owner.CurrentSystem ?? owner.KnownShipSystem(ship));
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
        if (owner.HeatForKnownShip(ship) is { } actual && !ReferenceEquals(actual, this))
        {
            actual.ClearSelection(pilot, ship);
            return;
        }
        using var context = owner.UseHeatSystem(ship);
        owner.Run(
            () =>
            {
                var actual = owner.HeatForKnownShip(ship);
                if (!ReferenceEquals(actual, this))
                {
                    actual.ClearSelection(pilot, ship);
                }
                else
                {
                    ClearSelectionOwned(pilot, ship);
                }
            },
            nameof(ClearSelection)
        );
    }

    private void ClearSelectionOwned(long pilot, long ship)
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
        owner.Run(() => WarmOwned(), nameof(Warm));
    }

    private void WarmOwned()
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
                owner.OwnHeat(this, ship, loaded.SystemId);
                if (loaded.SystemId is int system && owner.IsSharedHeat(this))
                {
                    owner.SharedHeatLoaded(system);
                }
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
        if (owner.HeatForKnownShip(ship) is { } actual && !ReferenceEquals(actual, this))
        {
            actual.PublishCommitted(pilot, ship, state);
            return;
        }
        using var context = owner.UseHeatSystem(ship);
        _ships[ship] = new(pilot, ship, state, HasSavedState: true, Changed: false)
        {
            SystemId = _ships.GetValueOrDefault(ship)?.SystemId ?? owner.CurrentSystem,
        };
        owner.OwnHeat(this, ship, _ships[ship].SystemId);
        if (_ships[ship].SystemId is int system && owner.IsSharedHeat(this))
        {
            owner.SharedHeatLoaded(system);
        }
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
        if (owner.HeatForKnownShip(ship) is { } actual && !ReferenceEquals(actual, this))
        {
            actual.PublishResident(ship, state);
            return;
        }
        using var context = owner.UseHeatSystem(ship);
        _ships[ship] = _ships[ship] with { State = state };
        _active.Add(ship);
        _dirty.Add(ship);
        owner.ObjectsChanged();
    }

    internal ShipHeatSnapshot Read(long pilot, long ship, long now)
    {
        return owner.Run(() => ReadOwned(pilot, ship, now), nameof(Read));
    }

    private ShipHeatSnapshot ReadOwned(long pilot, long ship, long now)
    {
        using var ownership = owner.Enter();
        if (!_ships.TryGetValue(ship, out var loaded))
        {
            loaded = Load(ship, now, false, expectedPilot: pilot) ?? throw new InvalidDataException("Read heat only on an owned ship.");
            if (owner.CurrentSystem is int current && loaded.SystemId is int loadedSystem && current != loadedSystem)
            {
                throw new InvalidDataException("The ship's system membership changed.");
            }
            _ships[ship] = loaded;
            owner.OwnHeat(this, ship, loaded.SystemId);
            if (loaded.SystemId is int system && owner.IsSharedHeat(this))
            {
                owner.SharedHeatLoaded(system);
            }
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

    private readonly ResidentMap<long, ConditionView> _conditionViews = [];

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

    internal void Advance(long now, TransactionParticipants? participants = null)
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
                if (participants is not null && !Participates(participants, previous))
                {
                    continue;
                }
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
                    using CatalogDbContext catalog = new(database.CatalogPath);
                    var affected = new TransactionParticipants(pilots: [loaded.PilotId], ships: [ship]);
                    ShipHeatCheckpoint checkpoint = new([new(ship, next)]);
                    if (owner.SqlConnections == 0)
                    {
                        using WorldActionScope transaction = new(database, affected);
                        checkpoint.Stage(transaction.World);
                        foreach (var module in burned)
                        {
                            ModuleConditionPersistence.BurnOut(
                                transaction.World,
                                catalog,
                                module.ItemId,
                                module.BurnedAt ?? next.UpdatedAt
                            );
                        }
                        transaction.Commit();
                    }
                    else
                    {
                        // The direct SQL adapter settles deadlines before starting its transaction.
                        using WorldDbContext world = new(database.Path) { Participants = affected };
                        using var transaction = world.Database.BeginTransaction();
                        checkpoint.Apply(world);
                        foreach (var module in burned)
                        {
                            ModuleConditionPersistence.ApplyDamage(world, catalog, module.ItemId, 0, module.BurnedAt ?? next.UpdatedAt);
                        }
                        world.SaveChanges();
                        transaction.Commit();
                    }
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
            if (owner.Economy.IsLoaded && owner.SqlConnections == 0)
            {
                using CatalogDbContext residentCatalog = new(database.CatalogPath);
                return owner.Economy.Inspect(
                    action => ShipHeatPersistence.Load(action, residentCatalog, ship, now, liveChange, current, expectedPilot),
                    new(ships: [ship])
                );
            }
            using WorldDbContext world = new(database.Path) { Participants = new(ships: [ship]) };
            world.Database.OpenConnection();
            using CatalogDbContext catalog = new(database.CatalogPath);
            var loaded = ShipHeatPersistence.Load(world, catalog, ship, now, liveChange, current, expectedPilot);
            return loaded;
        }
        finally
        {
            _internal--;
        }
    }

    internal ShipHeatCheckpoint? Capture() =>
        owner.IsSharedHeat(this) && owner.CurrentSystem is null ? owner.CaptureSystemHeat() : CaptureOwn();

    internal void AcknowledgeEconomic(ShipHeatProjection projection)
    {
        if (_ships.TryGetValue(projection.ShipId, out var loaded) && loaded.State == projection.State)
        {
            _dirty.Remove(projection.ShipId);
        }
    }

    internal ShipHeatCheckpoint? CaptureOwn() =>
        _dirty.Count == 0
            ? null
            : new(_dirty.Where(_ships.ContainsKey).Select(id => new ShipHeatProjection(id, _ships[id].State)).ToArray());

    internal ShipHeatProjection? CaptureShip(long ship) =>
        _dirty.Contains(ship) && _ships.TryGetValue(ship, out var loaded) ? new(ship, loaded.State) : null;

    internal void TransferSystemTo(LiveShipHeat destination, int system)
    {
        foreach (long ship in _ships.Where(pair => pair.Value.SystemId == system).Select(pair => pair.Key).ToArray())
        {
            TransferShipTo(destination, ship, system);
        }
    }

    internal void TransferShipTo(LiveShipHeat destination, long ship, int system)
    {
        if (_ships.Remove(ship, out var loaded))
        {
            destination._ships[ship] = loaded with { SystemId = system };
        }
        if (_active.Remove(ship))
        {
            destination._active.Add(ship);
        }
        if (_dirty.Remove(ship))
        {
            destination._dirty.Add(ship);
        }
        if (_stale.Remove(ship))
        {
            destination._stale.Add(ship);
        }
        if (_reloadSaved.Remove(ship))
        {
            destination._reloadSaved.Add(ship);
        }
        foreach (var request in _requested.Where(pair => pair.Value.Ship == ship).ToArray())
        {
            destination._requested[request.Key] = request.Value;
            _requested.Remove(request.Key);
            _requestViews.Remove(request.Value);
            destination._requestViews.Remove(request.Value);
        }
        owner.OwnHeat(destination, ship, system);
    }

    internal void ForgetRequests(long ship)
    {
        foreach (var request in _requested.Where(pair => pair.Value.Ship == ship).ToArray())
        {
            _requested.Remove(request.Key);
            _requestViews.Remove(request.Value);
        }
    }

    internal void Forget(long ship)
    {
        ForgetRequests(ship);
        _ships.Remove(ship);
        _active.Remove(ship);
        _dirty.Remove(ship);
        _stale.Remove(ship);
        _reloadSaved.Remove(ship);
    }

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

    private bool Participates(TransactionParticipants participants, LoadedShipHeat ship) =>
        participants.Includes(ship.PilotId, ship.ShipId, ship.SystemId)
        || owner.Combat?.Participates(participants, ship.PilotId, ship.ShipId) == true;

    internal void Flush(TransactionParticipants? participants = null, WorldDbContext? target = null)
    {
        if (_internal != 0 || target?.EconomicProjection.Residents.Contains(this) == true)
        {
            return;
        }
        if (participants?.Systems.Count > 0)
        {
            var missing = _ships.Values.Where(s => s.SystemId is null).Select(s => s.ShipId).ToArray();
            if (missing.Length > 0)
            {
                using WorldDbContext locations = target?.ProjectionContext() ?? new(database.Path) { IsResidentProjection = true };
                foreach (
                    var row in locations
                        .Ships.Where(s => missing.Contains(s.Id))
                        .Select(s => new
                        {
                            s.Id,
                            System = s.Position == null ? null : s.Position.SystemId,
                            s.StationId,
                        })
                )
                {
                    int system = row.System ?? database.Catalog.FindStation(row.StationId)!.SolarSystemId;
                    _ships[row.Id] = _ships[row.Id] with { SystemId = system };
                }
            }
        }
        if (target is null)
        {
            Advance(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(), participants);
        }
        if (_dirty.Count == 0)
        {
            return;
        }
        _internal++;
        try
        {
            var ships = _dirty.Where(id => participants is null || Participates(participants, _ships[id])).ToArray();
            if (ships.Length == 0)
            {
                return;
            }
            using WorldDbContext world =
                target?.ProjectionContext() ?? new(database.Path) { IsResidentProjection = true, SynchronizesEconomicProjection = true };
            new ShipHeatCheckpoint(ships.Select(id => new ShipHeatProjection(id, _ships[id].State)).ToArray()).Apply(world);
            if (target is null)
            {
                _dirty.ExceptWith(ships);
            }
            else
            {
                target.EconomicProjection.AddResident(this, () => _dirty.ExceptWith(ships));
            }
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
