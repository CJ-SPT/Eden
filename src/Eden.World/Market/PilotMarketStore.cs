using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Market;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Market;

[Injectable]
internal sealed partial class PilotMarketStore(
    WorldDatabase database,
    TimeProvider clock,
    MarketChangeSignal signal,
    Eden.World.Capsuleers.CapsuleerActivitySource? activity = null
) : IPilotMarket, IPersonalMarket, IMarketDemandMaintenance
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

    private static CatalogStationDefinition Station(CatalogDbContext catalog, int id) => catalog.RequireStation(id);

    private static bool HasMarket(CatalogDbContext catalog, CatalogStationDefinition station) =>
        StationCapabilities.Has(catalog, station.OperationId, StationService.Market);

    private static CatalogStationDefinition TradeStation(CatalogDbContext catalog, Pilot pilot, int stationId)
    {
        if (pilot.ActiveShip.Position is not null || pilot.ActiveShip.DockedLocationId != stationId)
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

    private static CatalogStationDefinition PurchaseStation(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, int stationId)
    {
        var station = Station(catalog, stationId);
        int systemId =
            pilot.ActiveShip.Position?.SystemId
            ?? (
                pilot.ActiveShip.StructureId is long structureId
                    ? PilotStructureStore.Require(world, structureId).SystemId
                    : Station(catalog, pilot.ActiveShip.StationId).SolarSystemId
            );
        if (catalog.RequireSystem(systemId).RegionId != station.RegionId)
        {
            throw new InvalidDataException("Buy from a station in your current region.");
        }
        if (!HasMarket(catalog, station))
        {
            throw new InvalidDataException("This station does not provide a market.");
        }
        return station;
    }

    private void Seed(
        WorldDbContext world,
        CatalogDbContext catalog,
        CatalogStationDefinition station,
        int? typeId = null,
        bool includeDemand = true
    )
    {
        if (RegionalMarketStore.Enabled(world))
        {
            SeedInstitutional(world, catalog, station);
            new RegionalMarketStore(activity).Advance(world, catalog, station.RegionId, station.Id, Now, typeId, includeDemand);
            return;
        }
        if (world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId) is { TestingMarket: false })
        {
            SeedInstitutional(world, catalog, station);
            return;
        }
        var marker = world.Set<MarketStation>().Find(station.Id);
        if (marker?.SeedVersion == TestingMarketRules.SeedVersion)
        {
            Refresh(world, world.Set<NpcMarketOrder>().Where(o => o.StationId == station.Id));
            return;
        }
        marker ??= world.Add(new MarketStation { StationId = station.Id, CreatedAt = Now }).Entity;
        var existing = world
            .Set<NpcMarketOrder>()
            .Where(o => o.StationId == station.Id && o.Kind == NpcMarketKind.Development)
            .ToDictionary(o => (o.TypeId, o.Bid));
        var knownTypes = catalog.Definitions.Items.Keys.ToHashSet();
        var types = catalog.RecordsInSourceOrder(CatalogData.Client.Types);
        foreach (var record in types)
        {
            int id = checked((int)record.Key);
            var type = record.Value;
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
            order.PriceCents = world.Settings.DevelopmentSupply.TestingPriceCents;
            order.Remaining = world.Settings.DevelopmentSupply.TestingStock;
            order.Entered = world.Settings.DevelopmentSupply.TestingStock;
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
                        RegionId = station.RegionId,
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
        if (
            RegionalMarketStore.Enabled(world)
            || world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId) is { TestingMarket: false }
        )
        {
            int stock = world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.InstitutionalStock;
            NpcSupplyRules.ValidateStock(stock);
            long institutionalCutoff = clock.GetUtcNow().AddDays(-MarketDefinitions.NpcOrderDays).UtcDateTime.ToFileTimeUtc();
            long issued = Now;
            var refreshed = orders
                .Where(o => o.Kind == NpcMarketKind.Institutional && !o.Bid && o.Issued > 0)
                .Where(o => o.Remaining != stock || o.Entered != stock || o.Issued <= institutionalCutoff)
                .AsTracking()
                .ToArray();
            foreach (var order in refreshed)
            {
                order.Remaining = stock;
                order.Entered = stock;
                if (order.Issued <= institutionalCutoff)
                {
                    order.Issued = issued;
                }
            }
            world.SaveChanges();
            return;
        }
        long cutoff = clock.GetUtcNow().AddDays(-MarketDefinitions.NpcOrderDays).UtcDateTime.ToFileTimeUtc();
        long now = Now;
        int[] retiredSkills = database
            .PublishedSkills.Values.Where(skill => skill.Deleted || !skill.Definition.Published)
            .Select(skill => skill.Definition.TypeId)
            .ToArray();
        // Refresh only stations upgraded to this policy, leaving historical NPC bids finite.
        orders
            .Where(o =>
                o.Kind == NpcMarketKind.Development
                && !o.Bid
                && !retiredSkills.Contains(o.TypeId)
                && world.Set<MarketStation>().Any(s => s.StationId == o.StationId && s.SeedVersion == TestingMarketRules.SeedVersion)
            )
            .Where(o =>
                o.Remaining != world.Settings.DevelopmentSupply.TestingStock
                || o.PriceCents != world.Settings.DevelopmentSupply.TestingPriceCents
                || o.Issued <= cutoff
            )
            .ExecuteUpdate(setters =>
                setters
                    .SetProperty(o => o.Remaining, world.Settings.DevelopmentSupply.TestingStock)
                    .SetProperty(o => o.Entered, world.Settings.DevelopmentSupply.TestingStock)
                    .SetProperty(o => o.PriceCents, world.Settings.DevelopmentSupply.TestingPriceCents)
                    .SetProperty(o => o.Issued, o => o.Issued <= cutoff ? now : o.Issued)
            );
    }

    public void CheckStation(long pilotId, int stationId)
    {
        if (Eden.World.Runtime.LiveWorld.Find(database.Path) is { } live)
        {
            ResidentCheckStation(live, pilotId, stationId);
            return;
        }
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        _ = TradeStation(catalog, Pilot(world, pilotId), stationId);
    }

    public long Balance(long pilotId) => new PilotWalletReader(database).Balance(pilotId);

    public IReadOnlyList<WalletEntryDefinition> EntryTypes() => new PilotWalletReader(database).EntryTypes();

    public IReadOnlyList<MarketOrder> Orders(long pilotId, int? typeId = null) => ReadOrders(pilotId, typeId, null).Orders;

    public IReadOnlyList<MarketOrder> BestAsks(long pilotId, MarketScope scope)
    {
        var live = LiveWorld.Find(database.Path);
        if (live is null)
        {
            return ReadOrders(pilotId, null, scope).Orders;
        }
        long catalog = database.Catalog.Identity.Generation;
        long now = Now;
        long active = live.CapsuleerActivity.ReadElapsed() ?? 0;
        if (
            live.SqlConnections == 0
            && live.MarketSummaries.TryRead(pilotId, scope, catalog, database.Settings, now, active, out var cached)
        )
        {
            return cached;
        }
        return live.Run(() =>
        {
            using var owner = live.EnterShared("market summary");
            long ownedCatalog = database.Catalog.Identity.Generation;
            long ownedNow = Now;
            long ownedActive = live.CapsuleerActivity.ReadElapsed() ?? 0;
            bool publish = live.SqlConnections == 0;
            if (
                publish
                && live.MarketSummaries.TryRead(pilotId, scope, ownedCatalog, database.Settings, ownedNow, ownedActive, out var view)
            )
            {
                return view;
            }
            var summary = ResidentReadOrders(live, pilotId, null, scope);
            if (publish)
            {
                live.MarketSummaries.Publish(summary, scope, ownedCatalog, database.Settings, ownedNow, ownedActive);
            }
            return summary.Orders;
        });
    }

    private MarketSummary ReadOrders(long pilotId, int? typeId, MarketScope? scope)
    {
        if (Eden.World.Runtime.LiveWorld.Find(database.Path) is { } live)
        {
            return ResidentReadOrders(live, pilotId, typeId, scope);
        }
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("market.summaryLoad");
        using WorldDbContext world = new(database.Path) { Participants = TransactionParticipants.None };
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var station = Station(catalog, pilot.ActiveShip.StationId);
        if (pilot.ActiveShip.Position is null && HasMarket(catalog, station))
        {
            Seed(world, catalog, station, typeId, includeDemand: scope is null);
        }
        int system = pilot.ActiveShip.Position?.SystemId ?? station.SolarSystemId;
        int region = catalog.RequireSystem(system).RegionId;
        var query = world.Set<NpcMarketOrder>().AsNoTracking().Where(o => o.RegionId == region);
        var kind = world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId) is { TestingMarket: false }
            ? NpcMarketKind.Institutional
            : NpcMarketKind.Development;
        bool progressive = RegionalMarketStore.Enabled(world);
        if (progressive)
        {
            var regional = new RegionalMarketStore(activity);
            var knownStations = world
                .Set<NpcMarketOrder>()
                .Where(o =>
                    o.RegionId == region
                    && o.Kind == NpcMarketKind.Regional
                    && (scope == null || !o.Bid)
                    && (!typeId.HasValue || o.TypeId == typeId.Value)
                )
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
        query = query.Where(o => progressive ? o.Kind == NpcMarketKind.Regional || o.Kind == NpcMarketKind.Institutional : o.Kind == kind);
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
                    other.TypeId == o.TypeId && (other.PriceCents < o.PriceCents || other.PriceCents == o.PriceCents && other.Id < o.Id)
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
            combined = combined.GroupBy(o => o.TypeId).Select(g => g.OrderBy(o => o.PriceCents).ThenBy(o => o.Id).First());
        }
        var book = combined.OrderBy(o => o.Id).ToArray();
        long expires = Math.Min(
            result.Select(o => o.Issued + TimeSpan.FromDays(MarketDefinitions.NpcOrderDays).Ticks).DefaultIfEmpty(long.MaxValue).Min(),
            personalRows.Select(o => o.Expires).DefaultIfEmpty(long.MaxValue).Min()
        );
        long activeUntil = long.MaxValue;
        if (scope.HasValue && progressive)
        {
            long elapsed =
                activity?.Read().ElapsedTicks ?? world.Set<Eden.Data.World.Capsuleers.SavedCapsuleerClock>().Single().ElapsedTicks;
            long? recovery = world
                .Set<SavedRegionalMarketStock>()
                .Where(s =>
                    world
                        .Set<NpcMarketOrder>()
                        .Any(o => o.Id == s.OrderId && o.RegionId == region && !o.Bid && o.Kind == NpcMarketKind.Regional && o.Issued > 0)
                )
                .Min(s => (long?)s.RefreshedTicks);
            activeUntil = RegionalMarketRules.NextCapacityChange(elapsed);
            if (recovery.HasValue)
            {
                activeUntil = Math.Min(activeUntil, recovery.Value + RegionalMarketRules.RecoveryIntervalTicks);
            }
        }
        transaction.Commit();
        return new(pilotId, pilot.ActiveShipId, station.Id, system, region, Array.AsReadOnly(book), expires, activeUntil);
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
            trade.Time,
            trade.WalletAmountCents
        );

    public IReadOnlyList<MarketTrade> Transactions(long pilotId, MarketTransactionQuery? query = null)
    {
        if (Eden.World.Runtime.LiveWorld.Find(database.Path) is { } live)
        {
            return ResidentTransactions(live, pilotId, query);
        }
        using WorldDbContext world = new(database.Path);
        if (query is { Limit: < 1 or > 1000 })
        {
            throw new InvalidDataException("Choose between one and 1000 market transactions.");
        }
        return Transactions(world, pilotId, new(0, query?.Before), query?.Limit);
    }

    internal static IReadOnlyList<MarketTrade> Transactions(
        WorldDbContext world,
        long pilotId,
        HistoryPeriod? period = null,
        int? limit = null
    )
    {
        long from = period?.From ?? 0;
        long until = period?.Until ?? long.MaxValue;
        return world
            .Set<MarketExecution>()
            .AsNoTracking()
            .Where(t => t.PilotId == pilotId && t.Time >= from && t.Time < until)
            .OrderByDescending(t => t.Id)
            .Take(limit ?? int.MaxValue)
            .AsEnumerable()
            .Select(Read)
            .ToArray();
    }

    public IReadOnlyList<MarketHistoryDay> History(long pilotId, IReadOnlyList<int> typeIds)
    {
        if (Eden.World.Runtime.LiveWorld.Find(database.Path) is { } live)
        {
            return ResidentHistory(live, pilotId, typeIds);
        }
        using WorldDbContext world = new(database.Path);
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
            where order.RegionId == region && typeIds.Contains(trade.TypeId) && trade.Time >= start && trade.Time <= end
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
                && stations.Contains(t.StationId)
                && typeIds.Contains(t.TypeId)
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

    private static PilotWallet Wallet(WorldDbContext world, long pilotId) =>
        world.Set<PilotWallet>().Find(pilotId) ?? world.Add(new PilotWallet { PilotId = pilotId }).Entity;

    private static decimal TaxRate(WorldDbContext world, CatalogDbContext catalog, long pilotId)
    {
        int skill = (int)MarketSkill.Accounting;
        int level = PilotSkillReader.Level(world, catalog, pilotId, skill);
        return MarketRules.SalesTax(level);
    }

    internal IReadOnlyList<MarketOrder> SupplyOrders(
        WorldDbContext world,
        CatalogDbContext catalog,
        int stationId,
        IReadOnlyList<int> types
    )
    {
        var station = Station(catalog, stationId);
        if (!HasMarket(catalog, station))
        {
            return [];
        }
        if (types.Count == 0)
        {
            return [];
        }
        bool progressive = RegionalMarketStore.Enabled(world);
        if (progressive)
        {
            SeedInstitutional(world, catalog, station);
            new RegionalMarketStore(activity).AdvanceTypes(
                world,
                catalog,
                station.RegionId,
                stationId,
                Now,
                types.Distinct().ToArray(),
                includeDemand: false
            );
        }
        else
        {
            Seed(world, catalog, station, includeDemand: false);
        }
        world.SaveChanges();
        var kind = world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId) is { TestingMarket: false }
            ? NpcMarketKind.Institutional
            : NpcMarketKind.Development;
        long cutoff = Now - TimeSpan.FromDays(MarketDefinitions.NpcOrderDays).Ticks;
        var npc = world
            .Set<NpcMarketOrder>()
            .Where(o =>
                o.StationId == stationId
                && !o.Bid
                && o.Remaining > 0
                && types.Contains(o.TypeId)
                && o.Issued > cutoff
                && (progressive ? o.Kind == NpcMarketKind.Institutional || o.Kind == NpcMarketKind.Regional : o.Kind == kind)
            )
            .AsEnumerable()
            .Select(o => new MarketOrder(
                o.Id,
                o.StationId,
                o.SystemId,
                o.RegionId,
                o.OwnerId,
                o.TypeId,
                false,
                o.PriceCents,
                o.Remaining,
                o.Entered,
                o.Issued
            ));
        var personal = world
            .Set<SavedPersonalOrder>()
            .Where(o =>
                o.StationId == stationId
                && !o.Bid
                && o.Remaining > 0
                && types.Contains(o.TypeId)
                && o.Status == PersonalOrderStatus.Open
                && o.Expires > Now
            )
            .AsEnumerable()
            .Select(o => o.Snapshot());
        return npc.Concat(personal).OrderBy(o => o.PriceCents).ThenBy(o => o.Issued).ThenBy(o => o.Id).ToArray();
    }

    public MarketResult Sell(long pilotId, IReadOnlyList<MarketSale> items) => ExecuteSell(pilotId, items, 0);

    public MarketResult Buy(long pilotId, int stationId, IReadOnlyList<MarketPurchase> items) => ExecuteBuy(pilotId, stationId, items, 0);
}
