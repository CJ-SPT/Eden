using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Industry;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Market;

internal sealed partial class PilotMarketStore
{
    private MarketResult ResidentBuy(
        Eden.World.Runtime.LiveWorld live,
        long pilotId,
        int stationId,
        IReadOnlyList<MarketPurchase> items,
        int days
    )
    {
        Expire();
        using var world = live.Economy.Begin();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var result = ExecuteBuy(world, catalog, pilotId, stationId, items, days);
        world.Commit();
        signal.Changed();
        return result;
    }

    private MarketResult ResidentSell(Eden.World.Runtime.LiveWorld live, long pilotId, IReadOnlyList<MarketSale> items, int days)
    {
        Expire();
        using var world = live.Economy.Begin();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var result = ExecuteSell(world, catalog, pilotId, items, days);
        world.Commit();
        signal.Changed();
        return result;
    }

    private void ResidentModify(Eden.World.Runtime.LiveWorld live, long pilotId, PersonalMarketModification request)
    {
        Expire();
        using var world = live.Economy.Begin();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var order = OwnedOrder(world, catalog, pilotId, request.OrderId);
        if (
            order.PriceCents != request.PreviousPriceCents
            || order.Remaining != request.Remaining
            || order.Issued != request.Issued
            || order.StationId != request.StationId
            || order.SystemId != request.SystemId
            || order.Bid != request.Bid
        )
        {
            throw new InvalidDataException("The market order changed. Refresh before modifying it.");
        }
        PersonalMarketRules.Validate(order.Remaining, request.PriceCents, order.DurationDays);
        if (Now < checked(order.Modified + TimeSpan.FromSeconds(PersonalMarketRules.ModificationSeconds).Ticks))
        {
            throw new InvalidDataException("Wait five minutes before modifying this order.");
        }
        var station = Station(catalog, order.StationId);
        Seed(world, catalog, station, order.TypeId);
        Payment(
            world,
            order,
            WalletEntryType.BrokersFee,
            -PersonalMarketRules.RelistFee(
                order.PriceCents,
                request.PriceCents,
                order.Remaining,
                BrokerRate(world, catalog, pilotId, station),
                PilotSkillReader.Level(world, catalog, pilotId, (int)MarketSkill.AdvancedBrokerRelations)
            ),
            station.OwnerId
        );
        if (order.Bid)
        {
            long escrow = checked(request.PriceCents * order.Remaining);
            Payment(world, order, WalletEntryType.MarketEscrow, order.EscrowCents - escrow);
            order.EscrowCents = escrow;
        }
        order.PriceCents = request.PriceCents;
        order.Modified = Now;
        // Reset price/time priority but preserve the original expiration deadline.
        // Issued remains the original client expiration anchor; Modified owns matching priority.
        List<InventoryChange> changes = [];
        if (!order.Bid)
        {
            // Previously resting listings retain price/time priority over this modification.
            SettleRestingSales(
                world,
                catalog,
                order.StationId,
                order.TypeId,
                PersonalMarketRules.MaintenanceBatch,
                order.Id,
                request.PriceCents,
                requireComplete: true
            );
        }
        MatchOrders(world, catalog, order, changes);
        Event(world, order, MarketOrderChangeKind.Modified, changes);
        world.SaveChanges();
        world.Commit();
        signal.Changed();
    }

    private void ResidentCancel(Eden.World.Runtime.LiveWorld live, long pilotId, long orderId, int regionId)
    {
        Expire();
        using var world = live.Economy.Begin();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var order = OwnedOrder(world, catalog, pilotId, orderId);
        if (order.RegionId != regionId)
        {
            throw new InvalidDataException("Choose the order's region.");
        }
        Close(world, catalog, order, PersonalOrderStatus.Cancelled);
        world.SaveChanges();
        world.Commit();
        signal.Changed();
    }

    private int ResidentExpire(Eden.World.Runtime.LiveWorld live, int limit = PersonalMarketRules.MaintenanceBatch)
    {
        if (limit is < 1 or > PersonalMarketRules.MaintenanceBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        if (NextExpiry > Now)
        {
            return 0;
        }
        using var world = live.Economy.Begin();
        using CatalogDbContext catalog = new(database.CatalogPath);
        int expired = Expire(world, catalog, limit);
        world.Commit();
        if (expired > 0)
        {
            signal.Changed();
        }
        return expired;
    }

    private WorldEconomySettings ResidentSettings(Eden.World.Runtime.LiveWorld live)
    {
        using var world = live.Economy.Begin();
        var settings = world.Set<SavedWorldEconomy>().Single();
        return new(settings.TestingMarket, settings.TestingGrants, settings.InstitutionalStock);
    }

    private long ResidentObserve(Eden.World.Runtime.LiveWorld live, long pilotId)
    {
        using var world = live.Economy.Begin();
        return world.MarketHead(pilotId);
    }

    private IReadOnlyList<PersonalMarketChange> ResidentChanges(Eden.World.Runtime.LiveWorld live, long pilotId, long after)
    {
        using var world = live.Economy.Begin();
        if (after >= world.MarketHead(pilotId))
        {
            return [];
        }
        return world
            .Set<SavedMarketChange>()
            .AsNoTracking()
            .Where(c => c.PilotId == pilotId && c.Id > after)
            .OrderBy(c => c.Id)
            .Take(PersonalMarketRules.ChangeBatch)
            .AsEnumerable()
            .Select(c => JsonSerializer.Deserialize<PersonalMarketChange>(c.Payload)! with { Id = c.Id })
            .ToArray();
    }

    private IReadOnlyList<MarketOrder> ResidentPersonalOrders(Eden.World.Runtime.LiveWorld live, long pilotId, bool history = false)
    {
        using var world = live.Economy.Begin();
        return world
            .Set<SavedPersonalOrder>()
            .AsNoTracking()
            .Where(o => o.PilotId == pilotId && (history ? o.Status != PersonalOrderStatus.Open : o.Status == PersonalOrderStatus.Open))
            .OrderByDescending(o => o.Id)
            .AsEnumerable()
            .Select(o => o.Snapshot())
            .ToArray();
    }

    private MarketEscrow ResidentEscrow(Eden.World.Runtime.LiveWorld live, long pilotId)
    {
        using var world = live.Economy.Begin();
        var orders = world.Set<SavedPersonalOrder>().Where(o => o.PilotId == pilotId && o.Status == PersonalOrderStatus.Open).ToArray();
        return new(orders.Sum(o => o.EscrowCents), orders.Where(o => !o.Bid).Sum(o => checked(o.PriceCents * o.Remaining)));
    }

    private MarketSummary ResidentReadOrders(Eden.World.Runtime.LiveWorld live, long pilotId, int? typeId, MarketScope? scope)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("market.summaryLoad");
        using var world = live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var station = Station(catalog, pilot.ActiveShip.StationId);
        if (pilot.ActiveShip.Position is null && HasMarket(catalog, station))
        {
            Seed(world, catalog, station, typeId, includeDemand: scope is null);
        }
        int system = pilot.ActiveShip.Position?.SystemId ?? station.SolarSystemId;
        int region = catalog.RequireSystem(system).RegionId;
        var regionOrders = world.Set<NpcMarketOrder>().AsNoTracking().Where(o => o.RegionId == region);
        var kind = world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId) is { TestingMarket: false }
            ? NpcMarketKind.Institutional
            : NpcMarketKind.Development;
        bool progressive = RegionalMarketStore.Enabled(world);
        if (progressive)
        {
            var regional = new RegionalMarketStore(activity);
            var knownStations = regionOrders
                .Where(o => o.Kind == NpcMarketKind.Regional && (scope == null || !o.Bid) && (!typeId.HasValue || o.TypeId == typeId.Value))
                .Select(o => o.StationId)
                .Distinct()
                .ToArray();
            var hub = RegionalMarketCatalog
                .Read(catalog)
                .Stations.SingleOrDefault(s => s.Station.RegionId == region && s.Role == RegionalMarketRole.Hub);
            foreach (int known in knownStations.Append(hub?.Station.Id ?? station.Id).Distinct().Where(id => id != station.Id))
            {
                SeedInstitutional(world, catalog, catalog.RequireStation(known));
                regional.Advance(world, catalog, region, known, Now, typeId, includeDemand: scope is null);
            }
        }
        var query = regionOrders.Where(o =>
            progressive ? o.Kind == NpcMarketKind.Regional || o.Kind == NpcMarketKind.Institutional : o.Kind == kind
        );
        if (typeId.HasValue)
        {
            query = query.Where(o => o.TypeId == typeId.Value);
        }
        query = scope switch
        {
            MarketScope.Station => query.Where(o => o.StationId == station.Id && !o.Bid),
            MarketScope.System => query.Where(o => o.SystemId == system && !o.Bid),
            MarketScope.Region => query.Where(o => !o.Bid),
            null => query,
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
        Refresh(world, query);
        long cutoff = clock.GetUtcNow().AddDays(-MarketDefinitions.NpcOrderDays).UtcDateTime.ToFileTimeUtc();
        var npcOrders = query
            .Where(o => o.Remaining > 0 && o.Issued > cutoff)
            .AsEnumerable()
            .Select(o => new MarketOrder(
                o.Id,
                o.StationId,
                o.SystemId,
                o.RegionId,
                o.OwnerId,
                o.TypeId,
                o.Bid,
                o.PriceCents,
                o.Remaining,
                o.Entered,
                o.Issued
            ));
        var result = (scope.HasValue ? BestAskPerType(npcOrders) : npcOrders).ToArray();
        var personal = world
            .Set<SavedPersonalOrder>()
            .AsNoTracking()
            .Where(o => o.RegionId == region && o.Status == PersonalOrderStatus.Open && o.Expires > Now);
        if (typeId.HasValue)
        {
            personal = personal.Where(o => o.TypeId == typeId.Value);
        }
        personal = scope switch
        {
            MarketScope.Station => personal.Where(o => o.StationId == station.Id && !o.Bid),
            MarketScope.System => personal.Where(o => o.SystemId == system && !o.Bid),
            MarketScope.Region => personal.Where(o => !o.Bid),
            _ => personal,
        };
        var personalRows = personal.ToArray();
        IEnumerable<MarketOrder> combined = result.Concat(personalRows.Select(o => o.Snapshot()));
        if (scope.HasValue)
        {
            combined = BestAskPerType(combined);
        }
        var book = combined.OrderBy(o => o.Id).ToArray();
        long expires = Math.Min(
            result.Select(o => o.Issued + TimeSpan.FromDays(MarketDefinitions.NpcOrderDays).Ticks).DefaultIfEmpty(long.MaxValue).Min(),
            personalRows.Select(o => o.Expires).DefaultIfEmpty(long.MaxValue).Min()
        );
        long activeUntil = long.MaxValue;
        if (scope.HasValue && progressive)
        {
            long elapsed = world.ActiveTicks;
            var stock = world.Set<SavedRegionalMarketStock>();
            long? recovery = regionOrders
                .Where(o => !o.Bid && o.Kind == NpcMarketKind.Regional && o.Issued > 0)
                .AsEnumerable()
                .Min(o => stock.Find(o.Id)?.RefreshedTicks);
            activeUntil = RegionalMarketRules.NextCapacityChange(elapsed);
            if (recovery.HasValue)
            {
                activeUntil = Math.Min(activeUntil, recovery.Value + RegionalMarketRules.RecoveryIntervalTicks);
            }
        }
        world.Commit();
        return new(pilotId, pilot.ActiveShipId, station.Id, system, region, Array.AsReadOnly(book), expires, activeUntil);
    }

    private static IEnumerable<MarketOrder> BestAskPerType(IEnumerable<MarketOrder> orders)
    {
        Dictionary<int, MarketOrder> best = [];
        foreach (var order in orders)
        {
            if (
                !best.TryGetValue(order.TypeId, out var current)
                || (order.PriceCents, order.Id).CompareTo((current.PriceCents, current.Id)) < 0
            )
            {
                best[order.TypeId] = order;
            }
        }
        return best.Values;
    }

    private void ResidentCheckStation(Eden.World.Runtime.LiveWorld live, long pilotId, int stationId)
    {
        using var world = live.Economy.Begin();
        using CatalogDbContext catalog = new(database.CatalogPath);
        _ = TradeStation(catalog, Pilot(world, pilotId), stationId);
    }

    private IReadOnlyList<MarketTrade> ResidentTransactions(
        Eden.World.Runtime.LiveWorld live,
        long pilotId,
        MarketTransactionQuery? query = null
    )
    {
        using var world = live.Economy.Begin();
        if (query is { Limit: < 1 or > 1000 })
        {
            throw new InvalidDataException("Choose between one and 1000 market transactions.");
        }
        return Transactions(world, pilotId, new(0, query?.Before), query?.Limit);
    }

    private IReadOnlyList<MarketHistoryDay> ResidentHistory(Eden.World.Runtime.LiveWorld live, long pilotId, IReadOnlyList<int> typeIds)
    {
        using var world = live.Economy.Begin();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        int system = pilot.ActiveShip.Position?.SystemId ?? Station(catalog, pilot.ActiveShip.StationId).SolarSystemId;
        int region = catalog.RequireSystem(system).RegionId;
        var now = clock.GetUtcNow().UtcDateTime;
        long end = now.ToFileTimeUtc();
        long start = now.Date.AddDays(-MarketDefinitions.HistoryDays).ToFileTimeUtc();
        // The ledger contains one execution per NPC trade. Aggregate actual
        // regional volume, not the seeded book, personal balances or remote data.
        var executions =
            from trade in world.Set<MarketExecution>().AsNoTracking()
            join order in world.Set<NpcMarketOrder>() on trade.OrderId equals order.Id
            where order.RegionId == region && Enumerable.Contains(typeIds, trade.TypeId) && trade.Time >= start && trade.Time <= end
            select trade;
        // Personal fills have two wallet entries, but only the seller contributes
        // regional price/volume. NPC executions already contain just one side.
        var stations = catalog.Stations().Where(s => s.RegionId == region).Select(s => s.Id).ToArray();
        var personalFills = world
            .Set<MarketExecution>()
            .AsNoTracking()
            .Where(t =>
                t.OrderId > PersonalMarketRules.FirstOrderId
                && !t.IsBuy
                && Enumerable.Contains(stations, t.StationId)
                && Enumerable.Contains(typeIds, t.TypeId)
                && t.Time >= start
                && t.Time <= end
            );
        var local = executions
            .AsEnumerable()
            .Concat(personalFills.AsEnumerable())
            .GroupBy(t => (t.TypeId, Date: DateTime.FromFileTimeUtc(t.Time).Date))
            .OrderBy(g => g.Key.TypeId)
            .ThenBy(g => g.Key.Date)
            .Select(g =>
            {
                long volume = g.Sum(t => (long)t.Quantity);
                decimal total = g.Sum(t => (decimal)t.PriceCents * t.Quantity);
                return new MarketHistoryDay(
                    g.Key.TypeId,
                    g.Key.Date.ToFileTimeUtc(),
                    g.Min(t => t.PriceCents),
                    g.Max(t => t.PriceCents),
                    total / volume,
                    volume,
                    g.Count()
                );
            })
            .ToArray();
        int[] knownTypes = typeIds.Where(catalog.Definitions.Items.ContainsKey).ToArray();
        return MarketHistoryCatalog.Merge(local, knownTypes, start, end);
    }

    private int ResidentAdvanceDemand(Eden.World.Runtime.LiveWorld live, int limit = PersonalMarketRules.MaintenanceBatch)
    {
        if (limit is < 1 or > PersonalMarketRules.MaintenanceBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        var snapshot = activity?.Read();
        if (snapshot is not { Active: true } || snapshot.ElapsedTicks < signal.DemandDeadline)
        {
            return 0;
        }
        using var world = live.Economy.Begin();
        if (!RegionalMarketStore.Enabled(world))
        {
            world.Commit();
            signal.ScheduleDemand(long.MaxValue, 0);
            return 0;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = Now;
        long cursor = signal.DemandCursor;
        var open = world
            .Set<SavedPersonalOrder>()
            .Where(o => !o.Bid && o.Status == PersonalOrderStatus.Open && o.Expires > now && o.Remaining > 0);
        var page = open.Where(o => o.Id > cursor).OrderBy(o => o.Id).Take(limit).ToArray();
        int settled = 0;
        foreach (var location in page.Select(o => new DemandLocation(o.StationId, o.TypeId)).Distinct())
        {
            Seed(world, catalog, Station(catalog, location.StationId), location.TypeId);
            settled += SettleRestingSales(world, catalog, location.StationId, location.TypeId, limit - settled);
            if (settled == limit)
            {
                break;
            }
        }
        bool more = page.Length == limit || settled == limit;
        long next = long.MaxValue;
        if (!more && open.Any())
        {
            long fallback = checked(snapshot.ElapsedTicks + RegionalMarketRules.RecoveryIntervalTicks);
            var boundaries =
                from stock in world.Set<SavedRegionalMarketStock>()
                join bid in world.Set<NpcMarketOrder>() on stock.OrderId equals bid.Id
                where
                    bid.Kind == NpcMarketKind.Regional
                    && bid.Bid
                    && bid.Issued > 0
                    && open.Any(o => o.StationId == bid.StationId && o.TypeId == bid.TypeId)
                select (long?)stock.RefreshedTicks;
            next = Math.Min(fallback, checked((boundaries.Min() ?? snapshot.ElapsedTicks) + RegionalMarketRules.RecoveryIntervalTicks));
        }
        world.SaveChanges();
        world.Commit();
        if (settled > 0)
        {
            signal.Changed(demand: false);
        }
        // Failed transactions never advance the cursor or deadline. A full page
        // continues next poll so expensive/unfillable listings cannot starve later IDs.
        signal.ScheduleDemand(more ? 0 : next, more ? page[^1].Id : 0);
        return settled;
    }
}
