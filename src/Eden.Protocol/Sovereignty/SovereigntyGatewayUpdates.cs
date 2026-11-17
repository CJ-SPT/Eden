using System.Collections.Concurrent;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Sovereignty;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.Gateway;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Sovereignty;

[Injectable(InjectionType.Singleton)]
public sealed class SovereigntyGatewayUpdates(ISovereigntyResources resources)
{
    private sealed record Observation(long PilotId, SovereigntyResourceView View);

    private sealed record SkyhookObservation(long PilotId, SovereigntySkyhook View);

    private readonly ConcurrentDictionary<(long Account, long Hub), Observation> _observations = new();
    private readonly ConcurrentDictionary<(long Account, long Skyhook), SkyhookObservation> _skyhooks = new();
    private readonly Dictionary<long, Dictionary<long, InventoryChange>> _inventory = [];

    internal void Observe(long accountId, long pilotId, SovereigntyResourceView view) =>
        _observations[(accountId, view.HubId)] = new(pilotId, view);

    internal void Observe(long accountId, long pilotId, SovereigntySkyhook view) => _skyhooks[(accountId, view.Id)] = new(pilotId, view);

    internal void Inventory(long pilotId, IReadOnlyList<InventoryChange> changes)
    {
        lock (_inventory)
        {
            if (!_inventory.TryGetValue(pilotId, out var items))
            {
                items = [];
                _inventory.Add(pilotId, items);
            }
            foreach (var change in changes)
            {
                items[change.Item.Id] = items.TryGetValue(change.Item.Id, out var old) ? change with { Previous = old.Previous } : change;
            }
        }
    }

    internal void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        InventoryChange[] changes;
        lock (_inventory)
        {
            changes = _inventory.Remove(context.Pilot.PilotId, out var items) ? items.Values.OrderBy(row => row.Item.Id).ToArray() : [];
        }
        foreach (var change in changes)
        {
            InventoryChangesWire.NotifyItemChange(context, change);
        }
    }

    public IReadOnlyList<Notice> Notices(long accountId)
    {
        List<Notice> result = [];
        foreach (var (key, before) in _observations.Where(row => row.Key.Account == accountId).ToArray())
        {
            try
            {
                if (!resources.OwnsPilot(accountId, before.PilotId))
                {
                    _observations.TryRemove(key, out _);
                    continue;
                }
                var current = resources.Read(before.PilotId, key.Hub);
                result.AddRange(SovereigntyResourceRequests.Notices(before.View, current, resources));
                _observations[key] = new(before.PilotId, current);
            }
            catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException)
            {
                _observations.TryRemove(key, out _);
            }
        }
        foreach (var (key, before) in _skyhooks.Where(row => row.Key.Account == accountId).ToArray())
        {
            try
            {
                if (!resources.OwnsPilot(accountId, before.PilotId))
                {
                    _skyhooks.TryRemove(key, out _);
                    continue;
                }
                var current = resources.Skyhook(before.PilotId, key.Skyhook);
                result.AddRange(SovereigntyResourceRequests.Notices(before.View, current));
                _skyhooks[key] = new(before.PilotId, current);
            }
            catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException)
            {
                _skyhooks.TryRemove(key, out _);
            }
        }
        return result;
    }

    public void Forget(long accountId)
    {
        long[] pilots = _observations
            .Where(row => row.Key.Account == accountId)
            .Select(row => row.Value.PilotId)
            .Concat(_skyhooks.Where(row => row.Key.Account == accountId).Select(row => row.Value.PilotId))
            .Distinct()
            .ToArray();
        foreach (var key in _skyhooks.Keys.Where(key => key.Account == accountId))
        {
            _skyhooks.TryRemove(key, out _);
        }
        foreach (var key in _observations.Keys.Where(key => key.Account == accountId))
        {
            _observations.TryRemove(key, out _);
        }
        lock (_inventory)
        {
            foreach (long pilot in pilots)
            {
                _inventory.Remove(pilot);
            }
        }
    }
}
