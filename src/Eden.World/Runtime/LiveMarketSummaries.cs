using Eden.Gameplay.Configuration;
using Eden.Gameplay.Market;
using Eden.World.Market;
using Eden.World.Runtime.Combat;

namespace Eden.World.Runtime;

// Immutable committed summaries can be read during the relational checkpoint copy.
// The small cache lock never encloses SQL or waits for a world/system owner.
internal sealed class LiveMarketSummaries
{
    private const int MaximumEntries = 512;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, PilotLocation> _locations = [];
    private readonly Dictionary<(MarketScope Scope, int Location), Entry> _views = [];
    private long _catalog;

    private sealed record Entry(MarketSummary Summary, long Catalog, WorldSettings Settings, long At, long ActiveAt);

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
            if (
                _catalog == catalog
                && _locations.TryGetValue(pilot, out var location)
                && _views.TryGetValue((scope, Location(location, scope)), out var entry)
                && entry.Catalog == catalog
                && entry.Settings == settings
                && now >= entry.At
                && now < entry.Summary.ExpiresAt
                && active >= entry.ActiveAt
                && active < entry.Summary.ActiveUntil
            )
            {
                orders = entry.Summary.Orders;
                return true;
            }
        }
        orders = [];
        return false;
    }

    internal void Publish(MarketSummary summary, MarketScope scope, long catalog, WorldSettings settings, long now, long active)
    {
        lock (_gate)
        {
            if (_catalog != catalog || _views.Count >= MaximumEntries || _locations.Count >= MaximumEntries)
            {
                _views.Clear();
                _locations.Clear();
            }
            _catalog = catalog;
            PilotLocation location = new(summary.ShipId, summary.StationId, summary.SystemId, summary.RegionId);
            _locations[summary.PilotId] = location;
            _views[(scope, Location(location, scope))] = new(summary, catalog, settings, now, active);
        }
    }

    internal bool HasViews
    {
        get
        {
            lock (_gate)
            {
                return _views.Count != 0;
            }
        }
    }

    internal void Observe(IReadOnlyCollection<CombatRowChange> changes, IReadOnlyList<MarketLocation>? locations)
    {
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
                _views.Clear();
            }
            else if (locations is not null)
            {
                foreach (var location in locations.Where(location => !location.Bid))
                {
                    _views.Remove((MarketScope.Station, location.Station));
                    _views.Remove((MarketScope.System, location.System));
                    _views.Remove((MarketScope.Region, location.Region));
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
