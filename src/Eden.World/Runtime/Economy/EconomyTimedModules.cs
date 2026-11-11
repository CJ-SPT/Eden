using System.Collections.Concurrent;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.World.Runtime.Combat;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record TimedModuleEntry(EconomyActiveFittedModule State, int System);

    private readonly SortedSet<(long Expires, long Module)> _moduleDeadlines = [];
    private readonly ConcurrentDictionary<int, SortedSet<(long Expires, long Module)>> _moduleSystemDeadlines = new();
    private readonly Dictionary<long, TimedModuleEntry> _timedEntries = [];
    private readonly Dictionary<long, HashSet<long>> _timedShips = [];

    private void ClearTimedModules()
    {
        _moduleDeadlines.Clear();
        _moduleSystemDeadlines.Clear();
        _timedEntries.Clear();
        _timedShips.Clear();
    }

    private void ReindexLoadedTimedModules()
    {
        // Bot tables load before hull/position tables. Resolve memberships only after the complete image is present.
        foreach (var entry in _timedEntries.Values.ToArray())
        {
            TimedModuleChanged(entry.State, entry.State);
        }
    }

    internal long NextModuleExpiry(int? system)
    {
        var deadlines = system is int id ? _moduleSystemDeadlines.GetValueOrDefault(id) : _moduleDeadlines;
        return deadlines is { Count: > 0 } ? deadlines.Min.Expires : long.MaxValue;
    }

    internal long[] DueModules(int? system, long now)
    {
        var deadlines = system is int id ? _moduleSystemDeadlines.GetValueOrDefault(id) : _moduleDeadlines;
        return deadlines is null || deadlines.Count == 0 || deadlines.Min.Expires > now
            ? []
            : deadlines.GetViewBetween((0, long.MinValue), (now, long.MaxValue)).Select(value => value.Module).ToArray();
    }

    private void TimedModuleChanged(EconomyActiveFittedModule? before, EconomyActiveFittedModule? after)
    {
        if (before is null && after is null)
        {
            return;
        }
        long module = after?.ModuleId ?? before!.ModuleId;
        if (_timedEntries.Remove(module, out var previous))
        {
            var key = (previous.State.ExpiresAt, module);
            _moduleDeadlines.Remove(key);
            var deadlines = _moduleSystemDeadlines[previous.System];
            deadlines.Remove(key);
            if (deadlines.Count == 0)
            {
                _moduleSystemDeadlines.TryRemove(previous.System, out _);
            }
            var members = _timedShips[previous.State.ShipId];
            members.Remove(module);
            if (members.Count == 0)
            {
                _timedShips.Remove(previous.State.ShipId);
            }
        }
        if (after is not { ExpiresAt: > 0 })
        {
            return;
        }
        var ship = Peek<Ship>(after.ShipId);
        int system =
            Peek<FlightPosition>(after.ShipId)?.SystemId
            ?? (ship is not null ? _database.Catalog.FindStation(ship.StationId)?.SolarSystemId : null)
            ?? 0;
        var next = (after.ExpiresAt, module);
        _timedEntries[module] = new(after, system);
        _moduleDeadlines.Add(next);
        if (!_moduleSystemDeadlines.TryGetValue(system, out var bySystem))
        {
            _moduleSystemDeadlines[system] = bySystem = [];
        }
        bySystem.Add(next);
        if (!_timedShips.TryGetValue(after.ShipId, out var byShip))
        {
            _timedShips[after.ShipId] = byShip = [];
        }
        byShip.Add(module);
    }

    private void ReindexTimedModules(IReadOnlyCollection<CombatRowChange> changes)
    {
        foreach (
            long ship in changes
                .Where(change =>
                    change.Table is "WorldShip" or "WorldPositionMembership" || !change.Resident && change.Table == "WorldPosition"
                )
                .Select(change => change.Id)
                .Distinct()
        )
        {
            if (!_timedShips.TryGetValue(ship, out var modules))
            {
                continue;
            }
            foreach (long module in modules.ToArray())
            {
                var state = _timedEntries[module].State;
                TimedModuleChanged(state, state);
            }
        }
    }
}
