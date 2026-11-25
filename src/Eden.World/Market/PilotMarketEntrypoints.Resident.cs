using System.Diagnostics;
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
    private const int DemandLocationsPerTurn = 8;
    private const int DemandSettlementsPerTurn = 16;
    private static readonly TimeSpan s_demandOwnerBudget = TimeSpan.FromMilliseconds(25);

    private MarketResult ResidentBuy(
        Eden.World.Runtime.LiveWorld live,
        long pilotId,
        int stationId,
        IReadOnlyList<MarketPurchase> items,
        int days,
        int minimumVolume
    )
    {
        Expire();
        using var world = live.Economy.Begin();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var result = ExecuteBuy(world, catalog, pilotId, stationId, items, days, minimumVolume: minimumVolume);
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
        var order = OwnedOrder(world, pilotId, request.OrderId);
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
        RequireRemoteTradeRange(world, catalog, Pilot(world, pilotId), station, MarketSkill.Daytrading);
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
        var order = OwnedOrder(world, pilotId, orderId);
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

    private static WorldEconomySettings ResidentSettings(LiveWorld live) =>
        live.Economy.Inspect(static world =>
        {
            var settings = world.Set<SavedWorldEconomy>().Single();
            return new WorldEconomySettings(settings.TestingMarket, settings.TestingGrants, settings.InstitutionalStock);
        });

    private static long ResidentObserve(LiveWorld live, long pilotId) => live.Economy.Inspect(world => world.MarketHead(pilotId));

    private static IReadOnlyList<PersonalMarketChange> ResidentChanges(LiveWorld live, long pilotId, long after)
    {
        if (!live.Economy.HasMarketChanges(pilotId, after))
        {
            return [];
        }
        return live.Economy.Inspect<IReadOnlyList<PersonalMarketChange>>(world =>
            after >= world.MarketHead(pilotId)
                ? []
                : world
                    .Filter<SavedMarketChange>(c => c.PilotId == pilotId && c.Id > after)
                    .OrderBy(c => c.Id)
                    .Take(PersonalMarketRules.ChangeBatch)
                    .AsEnumerable()
                    .Select(c => JsonSerializer.Deserialize<PersonalMarketChange>(c.Payload)! with { Id = c.Id })
                    .ToArray()
        );
    }

    private IReadOnlyList<MarketOrder> ResidentPersonalOrders(Eden.World.Runtime.LiveWorld live, long pilotId, bool history = false)
    {
        using var world = live.Economy.Begin();
        return world
            .Set<SavedPersonalOrder>()
            .AsNoTracking()
            .Where(o =>
                o.PilotId == pilotId
                && o.CorporationId == null
                && (history ? o.Status != PersonalOrderStatus.Open : o.Status == PersonalOrderStatus.Open)
            )
            .OrderByDescending(o => o.Id)
            .AsEnumerable()
            .Select(o => o.Snapshot())
            .ToArray();
    }

    private MarketEscrow ResidentEscrow(Eden.World.Runtime.LiveWorld live, long pilotId)
    {
        using var world = live.Economy.Begin();
        var orders = world
            .Set<SavedPersonalOrder>()
            .Where(o => o.PilotId == pilotId && o.CorporationId == null && o.Status == PersonalOrderStatus.Open)
            .ToArray();
        return new(orders.Sum(o => o.EscrowCents), orders.Where(o => !o.Bid).Sum(o => checked(o.PriceCents * o.Remaining)));
    }

    private MarketSummary? ChangedRegionAsks(LiveWorld live, long pilot, long catalog, long now, long active)
    {
        if (live.MarketSummaries.ReadDirtyRegion(pilot, catalog, database.Settings, now, active) is not { } changed)
        {
            return null;
        }
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("market.summaryChangedTypes");
        var previous = changed.Summary;
        var updated = changed.Types.Select(type => ResidentReadOrders(live, pilot, type, MarketScope.Region)).ToArray();
        if (
            updated.Any(summary => summary.RegionId != previous.RegionId) || !live.MarketSummaries.CanReplaceRegion(previous, changed.Types)
        )
        {
            return null;
        }
        var orders = previous
            .Orders.Where(order => !changed.Types.Contains(order.TypeId))
            .Concat(updated.SelectMany(summary => summary.Orders))
            .OrderBy(order => order.Id)
            .ToArray();
        return updated[0] with
        {
            Orders = orders.SequenceEqual(previous.Orders) ? previous.Orders : Array.AsReadOnly(orders),
            ExpiresAt = Math.Min(previous.ExpiresAt, updated.Min(summary => summary.ExpiresAt)),
            ActiveUntil = Math.Min(previous.ActiveUntil, updated.Min(summary => summary.ActiveUntil)),
        };
    }

    private MarketSummary ResidentReadOrders(Eden.World.Runtime.LiveWorld live, long pilotId, int? typeId, MarketScope? scope)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("market.summaryLoad");
        using var world = live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        // Ship.StationId still identifies the last NPC station while docked at an Upwell structure.
        var localStation = pilot.ActiveShip.StructureId is null ? Station(catalog, pilot.ActiveShip.StationId) : null;
        int stationId = localStation?.Id ?? 0;
        if (pilot.ActiveShip.Position is null && localStation is not null && HasMarket(catalog, localStation))
        {
            using var seed = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("market.seedLocal");
            Seed(world, catalog, localStation, typeId, includeDemand: scope is null);
        }
        int system = MarketSystem(world, catalog, pilot.ActiveShip);
        int region = catalog.RequireSystem(system).RegionId;
        var kind = MarketKind(world);
        if (world.Settings.Economy.StandardMarket)
        {
            SeedStandardOrders(world, catalog, region, typeId is int type ? [type] : null);
        }
        bool progressive = RegionalMarketStore.Enabled(world);
        if (progressive)
        {
            using var advance = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("market.advanceRegion");
            var regional = new RegionalMarketStore(activity);
            var knownStations = world
                .RegionalOrders(region, typeId)
                .Where(o => o.Kind == NpcMarketKind.Regional && (scope is null || !o.Bid))
                .Select(o => o.StationId)
                .Distinct()
                .ToArray();
            var hub = RegionalMarketCatalog.Read(catalog).Hubs.GetValueOrDefault(region);
            foreach (int known in knownStations.Append(hub?.Station.Id ?? stationId).Distinct().Where(id => id != 0 && id != stationId))
            {
                SeedInstitutional(world, catalog, catalog.RequireStation(known));
                regional.Advance(world, catalog, region, known, Now, typeId, includeDemand: scope is null);
            }
        }
        var query = world
            .Set<NpcMarketOrder>()
            .Where(o => o.RegionId == region)
            .Where(o => progressive ? o.Kind == NpcMarketKind.Regional || o.Kind == NpcMarketKind.Institutional : o.Kind == kind);
        if (typeId.HasValue)
        {
            query = query.Where(o => o.TypeId == typeId.Value);
        }
        query = scope switch
        {
            MarketScope.Station => query.Where(o => o.StationId == stationId && !o.Bid),
            MarketScope.System => query.Where(o => o.SystemId == system && !o.Bid),
            MarketScope.Region => query.Where(o => !o.Bid),
            null => query,
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
        Refresh(world, query);
        using var select = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("market.selectOrders");
        long cutoff = clock.GetUtcNow().AddDays(-MarketDefinitions.NpcOrderDays).UtcDateTime.ToFileTimeUtc();
        var npcOrders = world
            .RegionalOrders(region, typeId)
            .Where(o =>
                o.Remaining > 0
                && o.Issued > cutoff
                && o.Issued + (long)o.DurationDays * TimeSpan.TicksPerDay > Now
                && (progressive ? o.Kind is NpcMarketKind.Regional or NpcMarketKind.Institutional : o.Kind == kind)
                && InScope(o)
            );
        if (scope.HasValue)
        {
            npcOrders = BestAskPerType(npcOrders, static order => (order.TypeId, order.PriceCents, order.Id));
        }
        var result = npcOrders
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
                o.Issued,
                DurationDays: o.DurationDays,
                Range: o.Range,
                MinimumVolume: o.MinimumVolume
            ))
            .ToArray();
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
            MarketScope.Station => personal.Where(o => o.StationId == stationId && !o.Bid),
            MarketScope.System => personal.Where(o => o.SystemId == system && !o.Bid),
            MarketScope.Region => personal.Where(o => !o.Bid),
            _ => personal,
        };
        var personalRows = personal.ToArray();
        IEnumerable<MarketOrder> combined = result.Concat(personalRows.Select(o => o.Snapshot()));
        if (scope.HasValue)
        {
            combined = BestAskPerType(combined, static order => (order.TypeId, order.PriceCents, order.Id));
        }
        var book = combined.OrderBy(o => o.Id).ToArray();
        long expires = Math.Min(
            result.Select(o => o.Issued + TimeSpan.FromDays(o.DurationDays).Ticks).DefaultIfEmpty(long.MaxValue).Min(),
            personalRows.Select(o => o.Expires).DefaultIfEmpty(long.MaxValue).Min()
        );
        long activeUntil = long.MaxValue;
        if (scope.HasValue && progressive)
        {
            long elapsed = world.ActiveTicks;
            var stock = world.Set<SavedRegionalMarketStock>();
            long? recovery = world
                .RegionalOrders(region)
                .Where(o => !o.Bid && o.Kind == NpcMarketKind.Regional && o.Issued > 0)
                .Min(o => stock.Find(o.Id)?.RefreshedTicks);
            activeUntil = RegionalMarketRules.NextCapacityChange(elapsed);
            if (recovery.HasValue)
            {
                activeUntil = Math.Min(activeUntil, recovery.Value + RegionalMarketRules.RecoveryIntervalTicks);
            }
        }
        world.Commit();
        return new(pilotId, pilot.ActiveShipId, stationId, system, region, Array.AsReadOnly(book), expires, activeUntil);

        bool InScope(EconomyNpcMarketOrder order) =>
            scope switch
            {
                MarketScope.Station => order.StationId == stationId && !order.Bid,
                MarketScope.System => order.SystemId == system && !order.Bid,
                MarketScope.Region => !order.Bid,
                null => true,
                _ => throw new ArgumentOutOfRangeException(nameof(scope)),
            };
    }

    private static IEnumerable<T> BestAskPerType<T>(IEnumerable<T> orders, Func<T, (int Type, long Price, long Id)> key)
    {
        Dictionary<int, (T Order, long Price, long Id)> best = [];
        foreach (var order in orders)
        {
            var (type, price, id) = key(order);
            if (!best.TryGetValue(type, out var current) || (price, id).CompareTo((current.Price, current.Id)) < 0)
            {
                best[type] = (order, price, id);
            }
        }
        return best.Values.Select(static row => row.Order);
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
        int system = MarketSystem(world, catalog, pilot.ActiveShip);
        int region = catalog.RequireSystem(system).RegionId;
        var now = clock.GetUtcNow().UtcDateTime;
        long end = now.ToFileTimeUtc();
        long start = now.Date.AddDays(-MarketDefinitions.HistoryDays).ToFileTimeUtc();
        // Filter the indexed ledger before resolving orders. A resident LINQ join
        // materializes the entire NPC book before applying its trailing predicate.
        var trades = world
            .Set<MarketExecution>()
            .Where(t => Enumerable.Contains(typeIds, t.TypeId) && t.Time >= start && t.Time <= end)
            .ToArray();
        var orders = world.Set<NpcMarketOrder>();
        var executions = trades.Where(t => orders.Find(t.OrderId)?.RegionId == region);
        // Personal fills have two wallet entries, but only the seller contributes
        // regional price/volume. NPC executions already contain just one side.
        var personalFills = trades.Where(t =>
            t.OrderId > PersonalMarketRules.FirstOrderId && !t.IsBuy && catalog.FindStation(t.StationId)?.RegionId == region
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
        return MarketHistoryCatalog.Merge(local, knownTypes, start, end, world.Settings.Economy.SimulatedMarketHistory);
    }

    private int ResidentAdvanceDemand(Eden.World.Runtime.LiveWorld live, int limit = PersonalMarketRules.MaintenanceBatch)
    {
        if (limit is < 1 or > PersonalMarketRules.MaintenanceBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        if (database.Settings.Economy is { StandardMarket: true } or { ProgressiveMarkets: false })
        {
            return 0;
        }
        var snapshot = activity?.Read();
        if (snapshot is not { Active: true } || snapshot.ElapsedTicks < signal.DemandDeadline)
        {
            return 0;
        }
        using var owner = live.EnterShared("market demand");
        long started = Stopwatch.GetTimestamp();
        if (!live.Economy.Inspect(RegionalMarketStore.Enabled))
        {
            signal.ScheduleDemand(long.MaxValue, 0);
            return 0;
        }
        long now = Now;
        long cursor = signal.DemandCursor;
        var page = live.Economy.OpenSellOrdersAfter(cursor, now).Take(limit).ToArray();
        if (cursor == 0 && page.Length == 0)
        {
            signal.ScheduleDemand(long.MaxValue, 0);
            return 0;
        }
        int settlementLimit = Math.Min(limit, DemandSettlementsPerTurn);
        // Cheaper asks at the same location take priority, including sellers beyond this page of IDs.
        // Only those sellers can receive a payment. Importing unrelated combat here made a poll grow with every bot system.
        var locations = page.Select(o => new DemandLocation(o.StationId, o.TypeId)).Distinct().Take(DemandLocationsPerTurn);
        var sellers = locations.SelectMany(location => live.Economy.DemandSellers(location.StationId, location.TypeId, now));
        using var world = live.Economy.Begin(new(pilots: sellers));
        using CatalogDbContext catalog = new(database.CatalogPath);
        int settled = 0;
        HashSet<DemandLocation> attempted = [];
        RegionalMarketStore regional = new(activity);
        foreach (var entry in page)
        {
            if (attempted.Add(new(entry.StationId, entry.TypeId)))
            {
                if (
                    attempted.Count > DemandLocationsPerTurn
                    || attempted.Count > 1 && Stopwatch.GetElapsedTime(started) >= s_demandOwnerBudget
                )
                {
                    break;
                }
                var station = Station(catalog, entry.StationId);
                regional.Advance(world, catalog, station.RegionId, station.Id, now, entry.TypeId);
                settled += SettleRestingSales(world, catalog, station.Id, entry.TypeId, settlementLimit - settled);
                if (settled == settlementLimit)
                {
                    // This location may still have listings ready to fill.
                    break;
                }
            }
            cursor = entry.Id;
        }
        bool more = page.Length == limit || page.Length > 0 && cursor < page[^1].Id;
        long next = long.MaxValue;
        if (!more)
        {
            // Read the listings after settlement. A closed listing no longer needs a recovery wake.
            var demand = live
                .Economy.OpenSellOrdersAfter(0, now)
                .Where(o => world.PersonalOrderStillOpen(o.Id))
                .Select(o => new NpcMarketAssortment(o.StationId, o.TypeId, true, NpcMarketKind.Regional, 0))
                .ToHashSet();
            if (demand.Count > 0)
            {
                var stock = world.Set<SavedRegionalMarketStock>();
                long earliest = world
                    .NpcMarketOrders(demand)
                    .Where(bid => bid.Issued > 0)
                    .Select(bid => stock.Find(bid.Id)?.RefreshedTicks)
                    .Aggregate(snapshot.ElapsedTicks, (ticks, refreshed) => Math.Min(ticks, refreshed ?? ticks));
                next = checked(earliest + RegionalMarketRules.RecoveryIntervalTicks);
            }
        }
        world.SaveChanges();
        world.Commit();
        if (settled > 0)
        {
            signal.Changed(demand: false);
        }
        // Resume after the last fully processed listing. A failed transaction
        // leaves its work pending for the next poll.
        signal.ScheduleDemand(more ? 0 : next, more ? cursor : 0);
        return settled;
    }
}
