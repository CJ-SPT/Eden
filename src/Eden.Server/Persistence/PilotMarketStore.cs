using System.Globalization;
using System.Text.Json;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Protocol;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.Skills;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotMarketStore(WorldDatabase database, TimeProvider clock) : IPilotMarket
{
    private sealed record StarterOffer(int TypeId, string Name, long? BuyPriceCents, int Demand);

    private static readonly StarterOffer[] s_offers = ReadOffers();

    private static StarterOffer[] ReadOffers()
    {
        using var stream =
            typeof(PilotMarketStore).Assembly.GetManifestResourceStream("Eden.Server.Persistence.starter-market.json")
            ?? throw new InvalidDataException("Missing starter market policy.");
        return JsonSerializer.Deserialize<StarterOffer[]>(stream, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Invalid starter market policy.");
    }

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private static Pilot Pilot(WorldDbContext world, long id) =>
        world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == id);

    private static Station Station(CatalogDbContext catalog, int id) =>
        catalog.Set<Station>().Include(s => s.SolarSystem).ThenInclude(s => s.Constellation).Single(s => s.Id == id);

    private static bool HasMarket(CatalogDbContext catalog, Station station) =>
        StationCapabilities.Has(catalog, station.OperationId, StationService.Market);

    private static Station TradeStation(CatalogDbContext catalog, Pilot pilot, int stationId)
    {
        if (pilot.ActiveShip.Position is not null || pilot.ActiveShip.StationId != stationId)
        {
            throw new InvalidDataException("Trade from your current station while docked.");
        }
        var station = Station(catalog, stationId);
        if (!HasMarket(catalog, station))
        {
            throw new InvalidDataException("This station does not provide a market.");
        }
        return station;
    }

    private void Seed(WorldDbContext world, CatalogDbContext catalog, Station station)
    {
        var marker = world.Set<MarketStation>().Find(station.Id);
        if (marker?.SeedVersion == TestingMarketRules.SeedVersion)
        {
            Refresh(world, world.Set<NpcMarketOrder>().Where(o => o.StationId == station.Id));
            return;
        }
        marker ??= world.Add(new MarketStation { StationId = station.Id, CreatedAt = Now }).Entity;
        var existing = world.Set<NpcMarketOrder>().Where(o => o.StationId == station.Id).ToDictionary(o => (o.TypeId, o.Bid));
        var knownTypes = catalog.Set<ItemType>().Select(t => t.Id).ToHashSet();
        var types = catalog.Set<SourceRecord>().Where(r => r.Dataset == "client/types").AsEnumerable();
        foreach (var record in types)
        {
            int id = int.Parse(record.Key, CultureInfo.InvariantCulture);
            var type = record.Read<ClientTypes>();
            int? group = type.MarketGroupID is > 0 and <= int.MaxValue ? (int)type.MarketGroupID : null;
            if (!knownTypes.Contains(id) || !TestingMarketRules.IsEligible(new(id, type.Published == 1, group)))
            {
                continue;
            }
            if (!existing.TryGetValue((id, false), out var order))
            {
                order = AddOrder(id, false);
            }
            // Reuse existing identities so the historical execution ledger remains valid.
            order.PriceCents = TestingMarketRules.PriceCents;
            order.Remaining = TestingMarketRules.Stock;
            order.Entered = TestingMarketRules.Stock;
            order.Issued = Now;
        }
        foreach (var offer in s_offers.Where(o => o.BuyPriceCents.HasValue && knownTypes.Contains(o.TypeId)))
        {
            if (existing.ContainsKey((offer.TypeId, true)))
            {
                continue;
            }
            if (offer.BuyPriceCents <= 0 || offer.Demand <= 0)
            {
                throw new InvalidDataException("Invalid NPC market demand.");
            }
            var order = AddOrder(offer.TypeId, true);
            order.PriceCents = offer.BuyPriceCents!.Value;
            order.Remaining = offer.Demand;
            order.Entered = offer.Demand;
        }
        marker.SeedVersion = TestingMarketRules.SeedVersion;
        world.SaveChanges();

        NpcMarketOrder AddOrder(int typeId, bool bid) =>
            world
                .Add(
                    new NpcMarketOrder
                    {
                        StationId = station.Id,
                        SystemId = station.SolarSystemId,
                        RegionId = station.SolarSystem.Constellation.RegionId,
                        OwnerId = station.OwnerId,
                        TypeId = typeId,
                        Bid = bid,
                        Issued = Now,
                    }
                )
                .Entity;
    }

    private void Refresh(WorldDbContext world, IQueryable<NpcMarketOrder> orders)
    {
        long cutoff = clock.GetUtcNow().AddDays(-MarketDefinitions.NpcOrderDays).UtcDateTime.ToFileTimeUtc();
        long now = Now;
        // Refresh only stations upgraded to this policy, leaving historical NPC bids finite.
        orders
            .Where(o =>
                !o.Bid && world.Set<MarketStation>().Any(s => s.StationId == o.StationId && s.SeedVersion == TestingMarketRules.SeedVersion)
            )
            .Where(o => o.Remaining != TestingMarketRules.Stock || o.PriceCents != TestingMarketRules.PriceCents || o.Issued <= cutoff)
            .ExecuteUpdate(setters =>
                setters
                    .SetProperty(o => o.Remaining, TestingMarketRules.Stock)
                    .SetProperty(o => o.Entered, TestingMarketRules.Stock)
                    .SetProperty(o => o.PriceCents, TestingMarketRules.PriceCents)
                    .SetProperty(o => o.Issued, o => o.Issued <= cutoff ? now : o.Issued)
            );
    }

    public void CheckStation(long pilotId, int stationId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        _ = TradeStation(catalog, Pilot(world, pilotId), stationId);
    }

    public long Balance(long pilotId) => new PilotWalletReader(database).Balance(pilotId);

    public IReadOnlyList<WalletEntryDefinition> EntryTypes() => new PilotWalletReader(database).EntryTypes();

    public IReadOnlyList<MarketOrder> Orders(long pilotId, int? typeId = null) => ReadOrders(pilotId, typeId, null);

    public IReadOnlyList<MarketOrder> BestAsks(long pilotId, MarketScope scope) => ReadOrders(pilotId, null, scope);

    private IReadOnlyList<MarketOrder> ReadOrders(long pilotId, int? typeId, MarketScope? scope)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var station = Station(catalog, pilot.ActiveShip.StationId);
        if (pilot.ActiveShip.Position is null && HasMarket(catalog, station))
        {
            Seed(world, catalog, station);
        }
        int system = pilot.ActiveShip.Position?.SystemId ?? station.SolarSystemId;
        int region = catalog.Set<SolarSystem>().Where(s => s.Id == system).Select(s => s.Constellation.RegionId).Single();
        var query = world.Set<NpcMarketOrder>().AsNoTracking().Where(o => o.RegionId == region);
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
        query = query.Where(o => o.Remaining > 0 && o.Issued > cutoff);
        if (scope.HasValue)
        {
            var candidates = query;
            query = query.Where(o =>
                !candidates.Any(other =>
                    other.TypeId == o.TypeId && (other.PriceCents < o.PriceCents || (other.PriceCents == o.PriceCents && other.Id < o.Id))
                )
            );
        }
        var result = query
            .OrderBy(o => o.Id)
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
            ))
            .ToArray();
        transaction.Commit();
        return result;
    }

    private static MarketTrade Read(MarketExecution trade) =>
        new(
            trade.Id,
            trade.PilotId,
            trade.OrderId,
            trade.StationId,
            trade.CounterpartyId,
            trade.TypeId,
            trade.IsBuy,
            trade.Quantity,
            trade.PriceCents,
            trade.TaxCents,
            trade.BalanceCents,
            trade.Time
        );

    public IReadOnlyList<MarketTrade> Transactions(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        return Transactions(world, pilotId);
    }

    internal static IReadOnlyList<MarketTrade> Transactions(WorldDbContext world, long pilotId)
    {
        return world
            .Set<MarketExecution>()
            .AsNoTracking()
            .Where(t => t.PilotId == pilotId)
            .OrderByDescending(t => t.Id)
            .AsEnumerable()
            .Select(Read)
            .ToArray();
    }

    public IReadOnlyList<MarketHistoryDay> History(long pilotId, IReadOnlyList<int> typeIds)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        int system = pilot.ActiveShip.Position?.SystemId ?? Station(catalog, pilot.ActiveShip.StationId).SolarSystemId;
        int region = catalog.Set<SolarSystem>().Where(s => s.Id == system).Select(s => s.Constellation.RegionId).Single();
        var now = clock.GetUtcNow().UtcDateTime;
        long end = now.ToFileTimeUtc();
        long start = now.Date.AddDays(-MarketDefinitions.HistoryDays).ToFileTimeUtc();
        // The ledger contains one execution per NPC trade. Aggregate actual
        // regional volume, not the seeded book, personal balances or remote data.
        var executions =
            from trade in world.Set<MarketExecution>().AsNoTracking()
            join order in world.Set<NpcMarketOrder>() on trade.OrderId equals order.Id
            where order.RegionId == region && typeIds.Contains(trade.TypeId) && trade.Time >= start && trade.Time <= end
            select trade;
        return executions
            .AsEnumerable()
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
    }

    private static PilotWallet Wallet(WorldDbContext world, long pilotId) =>
        world.Set<PilotWallet>().Find(pilotId) ?? world.Add(new PilotWallet { PilotId = pilotId }).Entity;

    private static decimal TaxRate(WorldDbContext world, CatalogDbContext catalog, long pilotId)
    {
        int skill = (int)MarketSkill.Accounting;
        int level = PilotSkillReader.Level(world, catalog, pilotId, skill);
        return MarketRules.SalesTax(level);
    }

    private NpcMarketOrder Match(WorldDbContext world, int stationId, int typeId, bool bid, long limit)
    {
        long cutoff = clock.GetUtcNow().AddDays(-MarketDefinitions.NpcOrderDays).UtcDateTime.ToFileTimeUtc();
        var order = world.Set<NpcMarketOrder>().SingleOrDefault(o => o.StationId == stationId && o.TypeId == typeId && o.Bid == bid);
        if (
            order is null
            || order.Remaining == 0
            || order.Issued <= cutoff
            || limit <= 0
            || (bid ? order.PriceCents < limit : order.PriceCents > limit)
        )
        {
            throw new InvalidDataException("No matching station order remains at that price. Refresh the market quote.");
        }
        return order;
    }

    public MarketResult Sell(long pilotId, IReadOnlyList<MarketSale> items)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var station = TradeStation(catalog, pilot, pilot.ActiveShip.StationId);
        if (
            items.Count == 0
            || items.Select(i => i.ItemId).Distinct().Count() != items.Count
            || items.Any(i => i.Quantity <= 0 || i.StationId != station.Id)
        )
        {
            throw new InvalidDataException("Select distinct owned stacks and positive quantities at your current station.");
        }
        Seed(world, catalog, station);
        var wallet = Wallet(world, pilotId);
        decimal rate = TaxRate(world, catalog, pilotId);
        List<InventoryChange> changes = [];
        List<MarketExecution> trades = [];
        foreach (var request in items)
        {
            var item = InventoryPersistence.OwnedStack(world, pilotId, request.ItemId);
            if (
                item is null
                || item.OwnerId != pilotId
                || item.TypeId != request.TypeId
                || item.Singleton
                || request.Quantity > item.Quantity
                || PilotIndustryStore.IsInstalled(world, item.Id)
                || !(
                    (item.LocationId == station.Id && item.FlagId == (int)InventoryFlag.Hangar)
                    || (
                        item.LocationId == pilot.ActiveShipId
                        && (item.FlagId == (int)InventoryFlag.Cargo || item.FlagId == (int)InventoryFlag.GeneralMiningHold)
                    )
                )
            )
            {
                throw new InvalidDataException("The selected stack or quantity is unavailable in your hangar or cargo.");
            }
            var order = Match(world, station.Id, item.TypeId, true, request.LimitPriceCents);
            var (quantity, gross, tax) = MarketRules.Fill(request.Quantity, order.Remaining, order.PriceCents, rate);
            wallet.Cents = checked(wallet.Cents + gross - tax);
            var before = PilotInventoryStore.Read(catalog, item);
            item.Quantity -= quantity;
            order.Remaining -= quantity;
            changes.Add(new(before with { Quantity = item.Quantity }, before));
            if (item.Quantity == 0)
            {
                world.Remove(item);
            }
            trades.Add(
                world
                    .Add(
                        new MarketExecution
                        {
                            PilotId = pilotId,
                            OrderId = order.Id,
                            StationId = station.Id,
                            CounterpartyId = order.OwnerId,
                            TypeId = item.TypeId,
                            Quantity = quantity,
                            PriceCents = order.PriceCents,
                            TaxCents = tax,
                            BalanceCents = wallet.Cents,
                            Time = Now,
                        }
                    )
                    .Entity
            );
        }
        PilotTaskTrackingStore.Record(world, catalog, pilotId, Now, TaskEvents.Sold, trades.Count);
        world.SaveChanges();
        transaction.Commit();
        return new(wallet.Cents, trades.Select(Read).ToArray(), changes);
    }

    public MarketResult Buy(long pilotId, int stationId, IReadOnlyList<MarketPurchase> items)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var station = TradeStation(catalog, pilot, stationId);
        if (items.Count == 0 || items.Select(i => i.TypeId).Distinct().Count() != items.Count || items.Any(i => i.Quantity <= 0))
        {
            throw new InvalidDataException("Select distinct item types and positive quantities to buy.");
        }
        Seed(world, catalog, station);
        var wallet = Wallet(world, pilotId);
        List<InventoryChange> changes = [];
        List<MarketExecution> trades = [];
        foreach (var request in items)
        {
            var order = Match(world, station.Id, request.TypeId, false, request.LimitPriceCents);
            var fill = MarketRules.Fill(request.Quantity, order.Remaining, order.PriceCents);
            int quantity = fill.Quantity;
            long cost = fill.GrossCents;
            if (wallet.Cents < cost)
            {
                throw new InvalidDataException("You do not have enough ISK for this purchase.");
            }
            wallet.Cents -= cost;
            order.Remaining = TestingMarketRules.Stock;
            changes.Add(
                InventoryPersistence.MergeOutput(world, catalog, pilotId, stationId, (int)InventoryFlag.Hangar, request.TypeId, quantity)
            );
            trades.Add(
                world
                    .Add(
                        new MarketExecution
                        {
                            PilotId = pilotId,
                            OrderId = order.Id,
                            StationId = stationId,
                            CounterpartyId = order.OwnerId,
                            TypeId = request.TypeId,
                            Quantity = quantity,
                            PriceCents = order.PriceCents,
                            IsBuy = true,
                            BalanceCents = wallet.Cents,
                            Time = Now,
                        }
                    )
                    .Entity
            );
        }
        PilotTaskTrackingStore.Record(world, catalog, pilotId, Now, TaskEvents.Bought, trades.Count);
        world.SaveChanges();
        transaction.Commit();
        return new(wallet.Cents, trades.Select(Read).ToArray(), changes);
    }
}
