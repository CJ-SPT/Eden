using System.Collections.Frozen;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Thermal;
using Eden.World.Catalog;
using Eden.World.Runtime.Combat;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Thermal;

namespace Eden.World.Runtime;

// Paid repair deadlines use resident actions. The immutable active set also
// gives resident volleys a cheap busy guard.
internal sealed class LiveNaniteRepairs(WorldDatabase database, TimeProvider clock, int? system = null)
{
    private IReadOnlyDictionary<long, NaniteRepairSchedule> _current = FrozenDictionary<long, NaniteRepairSchedule>.Empty;
    private IReadOnlySet<long> _ships = FrozenSet<long>.Empty;
    private IReadOnlySet<long> _pilots = FrozenSet<long>.Empty;
    private IReadOnlyDictionary<(long Pilot, long Ship), IReadOnlyList<NaniteRepairSchedule>> _views = FrozenDictionary<
        (long Pilot, long Ship),
        IReadOnlyList<NaniteRepairSchedule>
    >.Empty;
    private long _next = long.MaxValue;
    private bool _valid;
    private bool _membership;
    private long? _membershipAt;
    private int _internal;

    internal void Observe(IReadOnlyCollection<CombatRowChange> changes)
    {
        if (_internal != 0)
        {
            return;
        }
        bool scheduleChange = changes.Any(c => c.Table is "WorldNaniteRepair" or "schema");
        if (scheduleChange)
        {
            _valid = false;
        }
        bool position = changes.Any(c => c.Table == "WorldPositionMembership");
        bool item = changes.Any(c => c.Table == "WorldItemMembership");
        if (
            changes.Any(c =>
                c.Table == "schema"
                || c.Table == "WorldShip" && _ships.Contains(c.Id)
                || c.Table == "WorldPilot" && _pilots.Contains(c.Id)
                || position && c.Table == "WorldPosition" && _ships.Contains(c.Id)
                || item && c.Table == "WorldItem" && _current.ContainsKey(c.Id)
                || c.Table == "WorldModuleCondition" && _current.ContainsKey(c.Id)
            )
            || scheduleChange && (position || item)
        )
        {
            _membership = true;
            long at = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            _membershipAt = _membershipAt is long prior ? Math.Min(prior, at) : at;
        }
    }

    internal bool TryRepairing(long module, long now, out bool repairing)
    {
        repairing = _current.ContainsKey(module);
        return _valid && !_membership && now < _next;
    }

    // LiveWorld.Enter owns advancement before this coherent snapshot is read.
    internal IReadOnlyList<NaniteRepairSchedule> Read(long pilot, long ship) =>
        _views.GetValueOrDefault((pilot, ship)) ?? Array.Empty<NaniteRepairSchedule>();

    // The caller already holds the world owner. Resident volleys bypass the
    // SQL activation stores and must reject every selected bank member here.
    internal void RequireAvailable(long module, long now)
    {
        Advance(now);
        CheckAvailable(module);
    }

    internal void RequireAvailable(IEnumerable<long> modules, long now)
    {
        Advance(now);
        foreach (long module in modules)
        {
            CheckAvailable(module);
        }
    }

    private void CheckAvailable(long module)
    {
        if (_current.ContainsKey(module))
        {
            throw new InvalidDataException("A selected weapon is being repaired with nanite paste.");
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
            if (!_valid)
            {
                Load();
            }
            if (!_membership && _next > now)
            {
                return;
            }
            using var transaction = new WorldActionScope(database, new(pilots: _pilots, ships: _ships));
            var world = transaction.World;
            using CatalogDbContext catalog = new(database.CatalogPath);
            Dictionary<long, OwnedItem> items = [];
            Dictionary<long, double> damage = [];
            Dictionary<long, long> activeShips = [];
            if (_membership)
            {
                items = _current.Keys.Select(id => world.Set<OwnedItem>().Find(id)).OfType<OwnedItem>().ToDictionary(i => i.Id);
                damage = _current
                    .Keys.Select(id => world.Set<SavedModuleCondition>().Find(id))
                    .OfType<SavedModuleCondition>()
                    .ToDictionary(c => c.ModuleId, c => c.Damage);
                activeShips = _pilots
                    .Select(id => world.Set<Pilot>().Find(id))
                    .OfType<Pilot>()
                    .Where(p => p.ActiveShip.OwnerId == p.Id && p.ActiveShip.Position is not null)
                    .ToDictionary(p => p.Id, p => p.ActiveShipId);
            }
            foreach (var schedule in _current.Values)
            {
                bool cancel = false;
                if (_membership)
                {
                    var item = items.GetValueOrDefault(schedule.Key.ModuleId);
                    cancel =
                        !activeShips.TryGetValue(schedule.PilotId, out long active)
                        || active != schedule.ShipId
                        || item is null
                        || item.OwnerId != schedule.PilotId
                        || item.LocationId != schedule.ShipId
                        || ShipHeatPersistence.Rack(item.FlagId) is null
                        || !item.Singleton
                        || item.Quantity != 1
                        || ModuleConditionPersistence.Read(catalog, item, damage.GetValueOrDefault(item.Id))
                            is { BurnedOut: true }
                                or { Damage: 0 };
                }
                if (cancel || now >= schedule.Key.CompletesAt)
                {
                    if (
                        world.Set<SavedNaniteRepair>().Find(schedule.Key.ModuleId) is { } row
                        && NaniteRepairPersistence.Schedule(row).Key == schedule.Key
                    )
                    {
                        long at = cancel && _membershipAt is long boundary ? Math.Min(now, boundary) : now;
                        NaniteRepairPersistence.Finish(world, catalog, row, at);
                    }
                }
            }
            transaction.Commit();
            transaction.Dispose();
            _valid = false;
            Load();
            _membership = false;
            _membershipAt = null;
        }
        finally
        {
            _internal--;
        }
    }

    private void Load()
    {
        var schedules = WorldActionScope.Inspect(
            database,
            world =>
                world
                    .Filter<SavedNaniteRepair>(_ => true)
                    .AsEnumerable()
                    .Where(row => system is null || InSystem(world, row.ShipId, system.Value))
                    .Select(NaniteRepairPersistence.Schedule)
                    .ToArray()
        );
        _current = schedules.ToFrozenDictionary(s => s.Key.ModuleId);
        _ships = schedules.Select(s => s.ShipId).ToFrozenSet();
        _pilots = schedules.Select(s => s.PilotId).ToFrozenSet();
        _views = schedules
            .GroupBy(s => (s.PilotId, s.ShipId))
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<NaniteRepairSchedule>)Array.AsReadOnly(g.ToArray()));
        _next = schedules.Length == 0 ? long.MaxValue : schedules.Min(s => s.Key.CompletesAt);
        _membership = schedules.Length > 0;
        if (schedules.Length == 0)
        {
            _membershipAt = null;
        }
        _valid = true;
    }

    private bool InSystem(WorldAction world, long shipId, int systemId)
    {
        var ship = world.Ships.Find(shipId);
        return ship is not null && (ship.Position?.SystemId ?? database.Catalog.FindStation(ship.StationId)?.SolarSystemId) == systemId;
    }
}
