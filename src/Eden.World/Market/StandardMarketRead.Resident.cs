using Eden.Data.World.Market;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Market;

internal sealed partial class PilotMarketStore
{
    private const int MaximumIncrementalMarketTypes = 8;

    private sealed record StandardBook(
        long PilotId,
        long ShipId,
        int StationId,
        int SystemId,
        int RegionId,
        long Catalog,
        WorldSettings Settings,
        long Now,
        long Active,
        long? Revision,
        EconomyNpcMarketOrder[] Orders,
        SavedPersonalOrder[] Personal,
        (MarketSummary Summary, HashSet<int> Types)? Changed
    );

    private MarketSummary ReadStandardMarketBook(LiveWorld live, long pilotId, int? typeId, MarketScope? scope)
    {
        var (book, region) = CaptureStandardMarketBook(live, pilotId, typeId, scope);
        while (book is null)
        {
            PrepareStandardRegion(live, region);
            (book, region) = CaptureStandardMarketBook(live, pilotId, typeId, scope);
        }
        using var phase = SessionPerformance.Phase("market.projectStandardBook");
        var summary = ProjectStandardMarketBook(book, scope);
        if (scope is MarketScope view && book.Revision.HasValue && live.SqlConnections == 0)
        {
            live.MarketSummaries.Publish(summary, view, book.Catalog, book.Settings, book.Now, book.Active, book.Revision);
        }
        return summary;
    }

    private (StandardBook? Book, int Region) CaptureStandardMarketBook(LiveWorld live, long pilotId, int? typeId, MarketScope? scope)
    {
        using var phase = SessionPerformance.Phase("market.captureStandardBook");
        using var world = live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        int station = pilot.ActiveShip.StructureId is null ? pilot.ActiveShip.StationId : 0;
        int system = MarketSystem(world, catalog, pilot.ActiveShip);
        int region = catalog.RequireSystem(system).RegionId;
        if (typeId is int selected)
        {
            SeedStandardOrders(world, catalog, region, [selected]);
        }
        else if (!IsStandardRegionSeeded(world, region, database.MarketSnapshot.Manifest.CapturedAt.UtcTicks))
        {
            return (null, region);
        }
        long catalogGeneration = catalog.Definitions.Identity.Generation;
        var settings = database.Settings;
        long now = Now;
        long active = live.CapsuleerActivity.ReadElapsed() ?? 0;
        bool publish = scope.HasValue && live.SqlConnections == 0;
        var changed =
            publish && scope == MarketScope.Region
                ? live.MarketSummaries.ReadDirtyRegion(pilotId, catalogGeneration, settings, now, active)
                : null;
        if (changed is { } previous && (previous.Summary.RegionId != region || previous.Types.Count > MaximumIncrementalMarketTypes))
        {
            changed = null;
        }
        var orders =
            scope == MarketScope.Station ? world.StationMarketOrders(station, typeId)
            : changed is { Types: var types } ? world.RegionalOrdersForTypes(region, types)
            : world.RegionalOrders(region, typeId);
        var personal = world
            .Set<SavedPersonalOrder>()
            .AsNoTracking()
            .Where(order => order.RegionId == region && order.Status == PersonalOrderStatus.Open && order.Expires > now);
        if (typeId is int requested)
        {
            personal = personal.Where(order => order.TypeId == requested);
        }
        var personalRows = personal.AsEnumerable().Where(order => changed is null || changed.Value.Types.Contains(order.TypeId)).ToArray();
        var orderRows = orders.ToArray();
        world.Commit();
        long? revision = publish ? live.MarketSummaries.CaptureLocation(pilotId, pilot.ActiveShipId, station, system, region) : null;
        StandardBook book = new(
            pilotId,
            pilot.ActiveShipId,
            station,
            system,
            region,
            catalogGeneration,
            settings,
            now,
            active,
            revision,
            orderRows,
            personalRows,
            changed
        );
        return (book, region);
    }

    private static MarketSummary ProjectStandardMarketBook(StandardBook book, MarketScope? scope)
    {
        long cutoff = book.Now - TimeSpan.FromDays(MarketDefinitions.NpcOrderDays).Ticks;
        var npc = book.Orders.Where(order =>
            order.Kind == NpcMarketKind.Standard
            && order.RegionId == book.RegionId
            && order.Remaining > 0
            && order.Issued > cutoff
            && order.Issued + (long)order.DurationDays * TimeSpan.TicksPerDay > book.Now
            && InScope(order.StationId, order.SystemId, order.Bid)
        );
        if (scope.HasValue)
        {
            npc = BestAskPerType(npc, static order => (order.TypeId, order.PriceCents, order.Id));
        }
        var asks = npc.Select(static order => new MarketOrder(
                order.Id,
                order.StationId,
                order.SystemId,
                order.RegionId,
                order.OwnerId,
                order.TypeId,
                order.Bid,
                order.PriceCents,
                order.Remaining,
                order.Entered,
                order.Issued,
                DurationDays: order.DurationDays,
                Range: order.Range,
                MinimumVolume: order.MinimumVolume
            ))
            .ToArray();
        var personal = book.Personal.Where(order => InScope(order.StationId, order.SystemId, order.Bid)).ToArray();
        var rows = asks.Concat(personal.Select(order => order.Snapshot()));
        if (scope.HasValue)
        {
            rows = BestAskPerType(rows, static order => (order.TypeId, order.PriceCents, order.Id));
        }
        long expires = Math.Min(
            asks.Select(order => order.Issued + (long)order.DurationDays * TimeSpan.TicksPerDay).DefaultIfEmpty(long.MaxValue).Min(),
            personal.Select(order => order.Expires).DefaultIfEmpty(long.MaxValue).Min()
        );
        if (book.Changed is { } changed)
        {
            rows = rows.Concat(changed.Summary.Orders.Where(order => !changed.Types.Contains(order.TypeId)));
            expires = Math.Min(expires, changed.Summary.ExpiresAt);
        }
        var projected = rows.OrderBy(order => order.Id).ToArray();
        var result =
            book.Changed is { } previous && projected.SequenceEqual(previous.Summary.Orders)
                ? previous.Summary.Orders
                : Array.AsReadOnly(projected);
        return new(book.PilotId, book.ShipId, book.StationId, book.SystemId, book.RegionId, result, expires, long.MaxValue);

        bool InScope(int station, int system, bool bid) =>
            scope switch
            {
                MarketScope.Station => station == book.StationId && !bid,
                MarketScope.System => system == book.SystemId && !bid,
                MarketScope.Region => !bid,
                null => true,
                _ => throw new ArgumentOutOfRangeException(nameof(scope)),
            };
    }
}
