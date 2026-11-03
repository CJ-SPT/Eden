using System.Globalization;
using System.Text.Json;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
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
    private sealed record StarterOffer(int TypeId, string Name, long? BuyPriceCents, long? SellPriceCents, int Demand, int Stock);

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
        catalog
            .Set<SourceRecord>()
            .Find("client/stationoperations", station.OperationId.ToString(CultureInfo.InvariantCulture))
            ?.Read<ClientStationoperations>()
            .Services?.Contains((long)StationService.Market) == true;

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
        // This is Eden's finite starter liquidity, not a Tranquility price feed.
        // New policy offers may be added to visited stations. Retain every old
        // order, including empty/expired ones, so no existing stock is replenished.
        if (world.Set<MarketStation>().Find(station.Id) is null)
        {
            world.Add(new MarketStation { StationId = station.Id, CreatedAt = Now });
        }
        var seeded = world
            .Set<NpcMarketOrder>()
            .Where(o => o.StationId == station.Id)
            .Select(o => new { o.TypeId, o.Bid })
            .AsEnumerable()
            .Select(o => (o.TypeId, o.Bid))
            .ToHashSet();
        foreach (var offer in s_offers)
        {
            if (!catalog.Set<ItemType>().Any(t => t.Id == offer.TypeId))
            {
                continue;
            }
            foreach (bool bid in new[] { true, false })
            {
                long? price = bid ? offer.BuyPriceCents : offer.SellPriceCents;
                int quantity = bid ? offer.Demand : offer.Stock;
                if (!price.HasValue || seeded.Contains((offer.TypeId, bid)))
                {
                    continue;
                }
                if (price <= 0 || quantity <= 0)
                {
                    throw new InvalidDataException("Invalid starter market price or quantity.");
                }
                world.Add(
                    new NpcMarketOrder
                    {
                        StationId = station.Id,
                        SystemId = station.SolarSystemId,
                        RegionId = station.SolarSystem.Constellation.RegionId,
                        OwnerId = station.OwnerId,
                        TypeId = offer.TypeId,
                        Bid = bid,
                        PriceCents = price.Value,
                        Remaining = quantity,
                        Entered = quantity,
                        Issued = Now,
                    }
                );
            }
        }
        world.SaveChanges();
    }

    public void CheckStation(long pilotId, int stationId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        _ = TradeStation(catalog, Pilot(world, pilotId), stationId);
    }

    public long Balance(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        return world.Set<PilotWallet>().Find(pilotId)?.Cents ?? 0;
    }

    public IReadOnlyList<WalletEntryDefinition> EntryTypes()
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return catalog
            .Set<SourceRecord>()
            .Where(r => r.Dataset == "client/accountingentrytypes")
            .AsEnumerable()
            .Select(r =>
            {
                var type = r.Read<ClientAccountingentrytypes>();
                return new WalletEntryDefinition(
                    int.Parse(r.Key, CultureInfo.InvariantCulture),
                    type.EntryTypeNameID ?? 0,
                    type.Name ?? "",
                    type.Description ?? ""
                );
            })
            .OrderBy(t => t.Id)
            .ToArray();
    }

    public IReadOnlyList<MarketOrder> Orders(long pilotId)
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
        long cutoff = clock.GetUtcNow().AddDays(-MarketDefinitions.NpcOrderDays).UtcDateTime.ToFileTimeUtc();
        var result = world
            .Set<NpcMarketOrder>()
            .AsNoTracking()
            .Where(o => o.RegionId == region && o.Remaining > 0 && o.Issued > cutoff)
            .OrderBy(o => o.Id)
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
        long points = world.Set<PilotSkill>().Find(pilotId, skill)?.Points ?? 0;
        double rank = catalog.Set<TypeAttribute>().Find(skill, (int)DogmaAttributeId.SkillTimeConstant)?.Value ?? 1;
        int level = new TrainedSkill(skill, points, rank).Level;
        return MarketDefinitions.SalesTaxRate * (1 - MarketDefinitions.AccountingReductionPerLevel * level);
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
            var item = world.Set<OwnedItem>().Find(request.ItemId);
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
            int quantity = Math.Min(request.Quantity, order.Remaining);
            long gross = checked(order.PriceCents * quantity);
            long tax = checked((long)Math.Round(gross * rate, 0, MidpointRounding.ToEven));
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
        long nextId = PilotInventoryStore.NextId(world);
        List<InventoryChange> changes = [];
        List<MarketExecution> trades = [];
        foreach (var request in items)
        {
            var order = Match(world, station.Id, request.TypeId, false, request.LimitPriceCents);
            int quantity = Math.Min(request.Quantity, order.Remaining);
            long cost = checked(order.PriceCents * quantity);
            if (wallet.Cents < cost)
            {
                throw new InvalidDataException("You do not have enough ISK for this purchase.");
            }
            wallet.Cents -= cost;
            order.Remaining -= quantity;
            var stack = world
                .Set<OwnedItem>()
                .Where(i =>
                    i.OwnerId == pilotId
                    && i.LocationId == stationId
                    && i.FlagId == (int)InventoryFlag.Hangar
                    && i.TypeId == request.TypeId
                    && !i.Singleton
                )
                .OrderBy(i => i.Id)
                .FirstOrDefault();
            var before = stack is null ? null : PilotInventoryStore.Read(catalog, stack);
            if (stack is null)
            {
                stack = world
                    .Add(
                        new OwnedItem
                        {
                            Id = nextId++,
                            OwnerId = pilotId,
                            TypeId = request.TypeId,
                            LocationId = stationId,
                            FlagId = (int)InventoryFlag.Hangar,
                            Quantity = quantity,
                        }
                    )
                    .Entity;
            }
            else
            {
                stack.Quantity = checked(stack.Quantity + quantity);
            }
            changes.Add(new(PilotInventoryStore.Read(catalog, stack), before));
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
        world.SaveChanges();
        transaction.Commit();
        return new(wallet.Cents, trades.Select(Read).ToArray(), changes);
    }
}
