using Eden.Gameplay.Configuration;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Market;
using Eden.World.Runtime.Combat;

namespace Eden.World.Runtime;

// Immutable committed summaries can be read during the relational checkpoint copy.
// The small cache lock never encloses SQL or waits for a world/system owner.
internal sealed class LiveMarketSummaries
{
    private const int MaximumEntries = 512;
    internal const int MaximumRetainedOrders = 128 * 1024;
    internal const int MaximumIncrementalTypes = 8;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, PilotLocation> _locations = [];
    private readonly Dictionary<(MarketScope Scope, int Location), Entry> _views = [];
    private readonly LinkedList<(MarketScope Scope, int Location)> _recency = new();
    private int _retainedOrders;
    private long _catalog;
    private long _ordersRevision;

    private sealed record Entry(
        MarketSummary Summary,
        WorldSettings Settings,
        long At,
        long ActiveAt,
        LinkedListNode<(MarketScope Scope, int Location)> Recency
    )
    {
        internal HashSet<int> DirtyTypes { get; } = [];
    }

    private readonly record struct PilotLocation(long ShipId, int StationId, int SystemId, int RegionId);

    private static int Location(PilotLocation location, MarketScope scope) =>
        scope switch
        {
            MarketScope.Station => location.StationId,
            MarketScope.System => location.SystemId,
            MarketScope.Region => location.RegionId,
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };

    internal bool TryRead(
        long pilot,
        MarketScope scope,
        long catalog,
        WorldSettings settings,
        long now,
        long active,
        out IReadOnlyList<MarketOrder> orders
    )
    {
        lock (_gate)
        {
            if (Current(pilot, scope, catalog, settings, now, active) is { DirtyTypes.Count: 0 } entry)
            {
                orders = entry.Summary.Orders;
                return true;
            }
        }
        orders = [];
        return false;
    }

    private Entry? Current(long pilot, MarketScope scope, long catalog, WorldSettings settings, long now, long active)
    {
        if (
            _catalog != catalog
            || !_locations.TryGetValue(pilot, out var location)
            || !_views.TryGetValue((scope, Location(location, scope)), out var entry)
            || entry.Settings != settings
            || now < entry.At
            || now >= entry.Summary.ExpiresAt
            || active < entry.ActiveAt
            || active >= entry.Summary.ActiveUntil
        )
        {
            return null;
        }
        if (entry.Recency.Next is not null)
        {
            _recency.Remove(entry.Recency);
            _recency.AddLast(entry.Recency);
        }
        return entry;
    }

    internal (MarketSummary Summary, HashSet<int> Types)? ReadDirtyRegion(
        long pilot,
        long catalog,
        WorldSettings settings,
        long now,
        long active
    )
    {
        lock (_gate)
        {
            return Current(pilot, MarketScope.Region, catalog, settings, now, active) is { DirtyTypes.Count: > 0 } entry
                ? (entry.Summary, new(entry.DirtyTypes))
                : null;
        }
    }

    internal bool CanReplaceRegion(MarketSummary summary, HashSet<int> updated)
    {
        lock (_gate)
        {
            return _views.TryGetValue((MarketScope.Region, summary.RegionId), out var entry)
                && ReferenceEquals(entry.Summary, summary)
                && entry.DirtyTypes.IsSubsetOf(updated);
        }
    }

    internal long CaptureLocation(long pilot, long ship, int station, int system, int region)
    {
        lock (_gate)
        {
            TrackLocation(pilot, new(ship, station, system, region));
            return _ordersRevision;
        }
    }

    private void TrackLocation(long pilot, PilotLocation location)
    {
        if (_locations.Count >= MaximumEntries && !_locations.ContainsKey(pilot))
        {
            _locations.Clear();
        }
        _locations[pilot] = location;
    }

    internal void Publish(
        MarketSummary summary,
        MarketScope scope,
        long catalog,
        WorldSettings settings,
        long now,
        long active,
        long? capturedRevision = null
    )
    {
        lock (_gate)
        {
            PilotLocation location = new(summary.ShipId, summary.StationId, summary.SystemId, summary.RegionId);
            if (
                capturedRevision is long revision
                && (revision != _ordersRevision || _locations.GetValueOrDefault(summary.PilotId) != location)
            )
            {
                return;
            }
            if (_catalog != catalog)
            {
                ClearViews();
                _locations.Clear();
            }
            _catalog = catalog;
            TrackLocation(summary.PilotId, location);

            var key = (scope, Location(location, scope));
            RemoveView(key);
            int count = summary.Orders.Count;
            if (count > MaximumRetainedOrders)
            {
                // The completed reply still belongs to the caller. It can exceed the cache budget.
                return;
            }
            while (_views.Count >= MaximumEntries || _retainedOrders > MaximumRetainedOrders - count)
            {
                RemoveView(_recency.First!.Value);
            }
            var recency = new LinkedListNode<(MarketScope Scope, int Location)>(key);
            _views.Add(key, new(summary, settings, now, active, recency));
            _recency.AddLast(recency);
            _retainedOrders += count;
        }
    }

    private void RemoveView((MarketScope Scope, int Location) key)
    {
        if (_views.Remove(key, out var entry))
        {
            _recency.Remove(entry.Recency);
            _retainedOrders -= entry.Summary.Orders.Count;
        }
    }

    private void ClearViews()
    {
        _views.Clear();
        _recency.Clear();
        _retainedOrders = 0;
    }

    internal bool HasViews
    {
        get
        {
            lock (_gate)
            {
                return _views.Count != 0 || _locations.Count != 0;
            }
        }
    }

    internal void Observe(IReadOnlyCollection<CombatRowChange> changes, IReadOnlyList<MarketLocation>? locations)
    {
        if (
            locations is not { Count: > 0 }
            && !changes.Any(c =>
                c.Table
                    is "schema"
                        or "WorldMarketMembership"
                        or "WorldMarketUnlock"
                        or "WorldEconomy"
                        or "WorldConfigurationState"
                        or "WorldMarketOrder"
                        or "WorldPersonalOrder"
                        or "WorldRegionalMarketStock"
                        or "WorldPilot"
                        or "WorldShip"
                        or "WorldPositionMembership"
            )
        )
        {
            return;
        }
        using var phase = SessionPerformance.Phase("market.summaryInvalidation");
        lock (_gate)
        {
            if (
                changes.Any(c =>
                    c.Table is "schema" or "WorldMarketMembership" or "WorldMarketUnlock" or "WorldEconomy" or "WorldConfigurationState"
                )
                || locations is null
                    && changes.Any(c => c.Table is "WorldMarketOrder" or "WorldPersonalOrder" or "WorldRegionalMarketStock")
            )
            {
                _ordersRevision++;
                ClearViews();
            }
            else if (locations is not null)
            {
                if (locations.Any(location => !location.Bid))
                {
                    _ordersRevision++;
                }
                foreach (var location in locations.Where(location => !location.Bid))
                {
                    RemoveView((MarketScope.Station, location.Station));
                    RemoveView((MarketScope.System, location.System));
                    var key = (MarketScope.Region, location.Region);
                    if (
                        _views.TryGetValue(key, out var region)
                        && region.DirtyTypes.Add(location.TypeId)
                        && region.DirtyTypes.Count > MaximumIncrementalTypes
                    )
                    {
                        RemoveView(key);
                    }
                }
            }
            foreach (var change in changes)
            {
                if (change.Table == "schema")
                {
                    _locations.Clear();
                }
                else if (change.Table == "WorldPilot")
                {
                    _locations.Remove(change.Id);
                }
                else if (change.Table is "WorldShip" or "WorldPositionMembership")
                {
                    foreach (long pilot in _locations.Where(p => p.Value.ShipId == change.Id).Select(p => p.Key).ToArray())
                    {
                        _locations.Remove(pilot);
                    }
                }
            }
        }
    }
}
