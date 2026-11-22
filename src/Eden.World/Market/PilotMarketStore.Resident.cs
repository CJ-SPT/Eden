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
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Market;

internal sealed partial class PilotMarketStore
{
    private static Pilot Pilot(WorldAction world, long id) => world.Pilots.Find(id) ?? throw new InvalidDataException("Unknown pilot.");

    private static CatalogStationDefinition PurchaseStation(WorldAction world, CatalogDbContext catalog, Pilot pilot, int stationId)
    {
        var station = Station(catalog, stationId);
        int systemId =
            pilot.ActiveShip.Position?.SystemId
            ?? (
                pilot.ActiveShip.StructureId is long structureId
                    ? world.StructureSystemId(structureId)
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
        WorldAction world,
        CatalogDbContext catalog,
        CatalogStationDefinition station,
        int? typeId = null,
        bool includeDemand = true,
        bool scopedDevelopmentSupply = false
    )
    {
        if (world.Settings.Economy.StandardMarket)
        {
            SeedStandardOrders(ResidentStandardMarket(world), catalog, station.RegionId, typeId is int type ? [type] : null);
            return;
        }
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
        SeedDevelopment(world, catalog, station, scopedDevelopmentSupply ? typeId : null);
    }

    internal static bool UsesInstitutionalOffers(WorldAction world) =>
        RegionalMarketStore.Enabled(world) || MarketKind(world) == NpcMarketKind.Institutional;

    internal EconomyNpcMarketOrder? StarterOreBid(EconomyTransaction world, CatalogDbContext catalog, int stationId, int typeId)
    {
        var station = Station(catalog, stationId);
        long now = Now;
        if (world.Settings.UsesFreshUniverseMarket() && ReadBid() is { } current && StarterOreBidIsCurrent(world, catalog, current, now))
        {
            return current.Remaining > 0 ? current : null;
        }
        new RegionalMarketStore(activity).Advance(world, catalog, station.RegionId, stationId, now, typeId);
        var refreshed = ReadBid();
        return refreshed is { Remaining: > 0 } ? refreshed : null;

        EconomyNpcMarketOrder? ReadBid() =>
            world
                .StationMarketOrders(stationId, typeId)
                .SingleOrDefault(order => order.RegionId == station.RegionId && order.Kind == NpcMarketKind.Regional && order.Bid);
    }

    private static bool StarterOreBidIsCurrent(EconomyTransaction world, CatalogDbContext catalog, EconomyNpcMarketOrder order, long now)
    {
        if (
            order.Issued == 0
            || now - order.Issued >= TimeSpan.FromDays(MarketDefinitions.NpcOrderDays).Ticks
            || world.RegionalStockState(order.Id) is not { DemandVersion: 1 } state
            || world.ActiveTicks - state.RefreshedTicks >= RegionalMarketRules.RecoveryIntervalTicks
        )
        {
            return false;
        }
        var profiles = RegionalMarketCatalog.Read(catalog);
        if (
            !profiles.StationsById.TryGetValue(order.StationId, out var profile)
            || profiles.DemandFor(catalog, order.TypeId)
                is not { Category: InventoryCategory.Asteroid, RawResource: true, RequiresUnlock: false }
            || MarketSnapshotCatalog.Quotes.GetValueOrDefault(order.TypeId)?.BuyCents is not long reference
        )
        {
            return false;
        }
        long anchor = RegionalMarketRules.SnapshotAnchor(reference, profile.Role, bid: true);
        int capacity = RegionalMarketRules.StarterOreDemand(anchor);
        // During the same recovery interval the stored quantity and its quote already reflect same-action sales.
        return state.AnchorCents == anchor
            && order.Entered == capacity
            && order.Remaining <= capacity
            && order.PriceCents == RegionalMarketRules.Quote(anchor, bid: true, order.Remaining, capacity);
    }

    private void SeedDevelopment(WorldAction world, CatalogDbContext catalog, CatalogStationDefinition station, int? typeId)
    {
        var stations = world.Set<MarketStation>();
        var marker = stations.Find(station.Id);
        var orders = world.Set<NpcMarketOrder>().Where(o => o.StationId == station.Id);
        if (typeId is int selected)
        {
            orders = orders.Where(o => o.TypeId == selected);
        }
        if (marker?.SeedVersion == TestingMarketRules.SeedVersion)
        {
            Refresh(world, orders);
            return;
        }
        var existing = orders.Where(o => o.Kind == NpcMarketKind.Development).ToDictionary(o => (o.TypeId, o.Bid));
        var knownTypes = catalog.Definitions.Items;
        long now = Now;
        long cutoff = now - TimeSpan.FromDays(MarketDefinitions.NpcOrderDays).Ticks;
        bool seeded = false;
        if (typeId is int requested)
        {
            if (catalog.Read(CatalogData.Client.Types, (long)requested) is { } type)
            {
                SeedAsk(requested, type.Published == 1, type.MarketGroupID);
            }
        }
        else
        {
            foreach (var record in catalog.RecordsInSourceOrder(CatalogData.Client.Types))
            {
                SeedAsk(checked((int)record.Key), record.Value.Published == 1, record.Value.MarketGroupID);
            }
        }
        foreach (var offer in s_offers.Where(o => o.BuyPriceCents.HasValue && (typeId is null || o.TypeId == typeId)))
        {
            if (!knownTypes.ContainsKey(offer.TypeId) || existing.ContainsKey((offer.TypeId, true)))
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
            seeded = true;
        }
        if (typeId is null || seeded)
        {
            marker ??= world.Add(new MarketStation { StationId = station.Id, CreatedAt = now }).Entity;
            marker.SeedVersion = typeId is null ? TestingMarketRules.SeedVersion : TestingMarketRules.SelectedTypesSeedVersion;
        }
        world.SaveChanges();

        void SeedAsk(int id, bool published, long? marketGroup)
        {
            int? group = marketGroup is > 0 and <= int.MaxValue ? (int)marketGroup : null;
            if (
                !knownTypes.TryGetValue(id, out var item)
                || RegionalMarketRules.IsPlayerSuppliedIndustryInput(id, (InventoryCategory)item.CategoryId, item.GroupId)
                || !TestingMarketRules.IsEligible(new(id, published, group))
            )
            {
                return;
            }
            // Preserve existing order IDs, including those referenced by market executions.
            var order = existing.GetValueOrDefault((id, false)) ?? AddOrder(id, false);
            order.PriceCents = world.Settings.DevelopmentSupply.TestingPriceCents;
            order.Remaining = world.Settings.DevelopmentSupply.TestingStock;
            order.Entered = world.Settings.DevelopmentSupply.TestingStock;
            if (typeId is null || order.Issued <= cutoff || order.Issued == 0)
            {
                order.Issued = now;
            }
            seeded = true;
        }

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
                        Issued = now,
                    }
                )
                .Entity;
    }

    private void Refresh(WorldAction world, IQueryable<NpcMarketOrder> orders, int? institutionalStation = null)
    {
        if (world.Settings.Economy.StandardMarket)
        {
            return;
        }
        if (
            RegionalMarketStore.Enabled(world)
            || world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId) is { TestingMarket: false }
        )
        {
            int stock = world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.InstitutionalStock;
            NpcSupplyRules.ValidateStock(stock);
            long institutionalCutoff = clock.GetUtcNow().AddDays(-MarketDefinitions.NpcOrderDays).UtcDateTime.ToFileTimeUtc();
            long issued = Now;
            IEnumerable<NpcMarketOrder> refreshed;
            if (institutionalStation is int station && world is EconomyTransaction resident)
            {
                // Touch only the institutional rows whose stock or expiry needs updating.
                var orderIds = resident
                    .StationMarketOrders(station)
                    .Where(order =>
                        order is { Kind: NpcMarketKind.Institutional, Bid: false, Issued: > 0 }
                        && (order.Remaining != stock || order.Entered != stock || order.Issued <= institutionalCutoff)
                    )
                    .Select(order => order.Id)
                    .ToArray();
                var marketOrders = world.Set<NpcMarketOrder>();
                refreshed = orderIds.Select(id => marketOrders.Find(id)).OfType<NpcMarketOrder>();
            }
            else
            {
                refreshed = orders
                    .Where(o => o.Kind == NpcMarketKind.Institutional && !o.Bid && o.Issued > 0)
                    .Where(o => o.Remaining != stock || o.Entered != stock || o.Issued <= institutionalCutoff)
                    .AsTracking()
                    .ToArray();
            }
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
        var supply = world.Settings.DevelopmentSupply;
        int[] retiredSkills = database
            .PublishedSkills.Values.Where(skill => skill.Deleted || !skill.Definition.Published)
            .Select(skill => skill.Definition.TypeId)
            .ToArray();
        // Refresh only stations upgraded to this policy, leaving historical NPC bids finite.
        var stations = world.Set<MarketStation>();
        var refreshedOrders = orders
            .Where(o => o.Kind == NpcMarketKind.Development && !o.Bid && o.Issued > 0 && !Enumerable.Contains(retiredSkills, o.TypeId))
            .Where(o => o.Remaining != supply.TestingStock || o.PriceCents != supply.TestingPriceCents || o.Issued <= cutoff)
            .AsEnumerable()
            .Where(o =>
                stations.Find(o.StationId)?.SeedVersion is TestingMarketRules.SeedVersion or TestingMarketRules.SelectedTypesSeedVersion
            );
        foreach (var order in refreshedOrders)
        {
            order.Remaining = supply.TestingStock;
            order.Entered = supply.TestingStock;
            order.PriceCents = supply.TestingPriceCents;
            if (order.Issued <= cutoff)
            {
                order.Issued = now;
            }
        }
    }

    internal static IReadOnlyList<MarketTrade> Transactions(
        WorldAction world,
        long pilotId,
        HistoryPeriod? period = null,
        int? limit = null
    )
    {
        long from = period?.From ?? 0;
        long until = period?.Until ?? long.MaxValue;
        return world
            .Filter<MarketExecution>(t => t.PilotId == pilotId && t.Time >= from && t.Time < until)
            .OrderByDescending(t => t.Id)
            .Take(limit ?? int.MaxValue)
            .AsEnumerable()
            .Select(Read)
            .ToArray();
    }

    private static PilotWallet Wallet(WorldAction world, long pilotId) =>
        world.Set<PilotWallet>().Find(pilotId) ?? world.Add(new PilotWallet { PilotId = pilotId }).Entity;

    private static decimal TaxRate(WorldAction world, CatalogDbContext catalog, long pilotId)
    {
        int skill = (int)MarketSkill.Accounting;
        int level = PilotSkillReader.Level(world, catalog, pilotId, skill);
        return MarketRules.SalesTax(level);
    }

    internal IReadOnlyList<MarketOrder> SupplyOrders(
        WorldAction world,
        CatalogDbContext catalog,
        int stationId,
        IReadOnlyList<int> types,
        long? buyerId = null,
        int? buyerCorporationId = null
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
        int[] requested = types.Distinct().ToArray();
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
                requested,
                includeDemand: false
            );
        }
        else if (world.Settings.Economy.StandardMarket)
        {
            SeedStandardOrders(ResidentStandardMarket(world), catalog, station.RegionId, requested);
        }
        else if (MarketKind(world) == NpcMarketKind.Development)
        {
            foreach (int type in requested)
            {
                SeedDevelopment(world, catalog, station, type);
            }
        }
        else
        {
            Seed(world, catalog, station, includeDemand: false);
        }
        world.SaveChanges();
        var kind = MarketKind(world);
        if (world is EconomyTransaction resident)
        {
            return ResidentSupplyOrders(resident, stationId, requested, buyerId, buyerCorporationId, kind, progressive, Now);
        }

        long cutoff = Now - TimeSpan.FromDays(MarketDefinitions.NpcOrderDays).Ticks;
        var npc = world
            .Set<NpcMarketOrder>()
            .Where(o =>
                o.StationId == stationId
                && !o.Bid
                && o.Remaining > 0
                && Enumerable.Contains(types, o.TypeId)
                && o.Issued > cutoff
                && o.Issued + (long)o.DurationDays * TimeSpan.TicksPerDay > Now
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
                o.Issued,
                DurationDays: o.DurationDays,
                Range: o.Range,
                MinimumVolume: o.MinimumVolume
            ));
        var personal = world
            .Set<SavedPersonalOrder>()
            .Where(o =>
                o.StationId == stationId
                && !o.Bid
                && o.Remaining > 0
                && Enumerable.Contains(types, o.TypeId)
                && o.Status == PersonalOrderStatus.Open
                && o.Expires > Now
                && (buyerId == null || o.PilotId != buyerId)
                && (buyerCorporationId == null || o.CorporationId != buyerCorporationId)
            )
            .AsEnumerable()
            .Select(o => o.Snapshot());
        return SortSupplyOrders(npc.Concat(personal));
    }

    private static MarketOrder[] ResidentSupplyOrders(
        EconomyTransaction world,
        int stationId,
        IReadOnlyList<int> types,
        long? buyerId,
        int? buyerCorporationId,
        NpcMarketKind kind,
        bool progressive,
        long now
    )
    {
        long cutoff = now - TimeSpan.FromDays(MarketDefinitions.NpcOrderDays).Ticks;
        var stock =
            types.Count == 1
                ? world.StationMarketOrders(stationId, types[0])
                : world.StationMarketOrdersForTypes(stationId, types.ToHashSet());
        var npc = stock
            .Where(o =>
                !o.Bid
                && o.Remaining > 0
                && o.Issued > cutoff
                && o.Issued + (long)o.DurationDays * TimeSpan.TicksPerDay > now
                && (progressive ? o.Kind is NpcMarketKind.Institutional or NpcMarketKind.Regional : o.Kind == kind)
            )
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
                o.Issued,
                DurationDays: o.DurationDays,
                Range: o.Range,
                MinimumVolume: o.MinimumVolume
            ));
        var personal = types
            .SelectMany(type => world.StationPersonalOrders(stationId, type))
            .Where(o =>
                !o.Bid
                && o.Remaining > 0
                && o.Status == PersonalOrderStatus.Open
                && o.Expires > now
                && (buyerId == null || o.PilotId != buyerId)
                && (buyerCorporationId == null || o.CorporationId != buyerCorporationId)
            )
            .Select(o => new MarketOrder(
                o.Id,
                o.StationId,
                o.SystemId,
                o.RegionId,
                o.CorporationId ?? o.PilotId,
                o.TypeId,
                false,
                o.PriceCents,
                o.Remaining,
                o.Entered,
                o.Issued,
                o.DurationDays,
                o.EscrowCents,
                IsCorporation: o.CorporationId.HasValue
            ));
        return SortSupplyOrders(npc.Concat(personal));
    }

    private static MarketOrder[] SortSupplyOrders(IEnumerable<MarketOrder> offers) =>
        offers.OrderBy(o => o.PriceCents).ThenBy(o => o.Issued).ThenBy(o => o.Id).ToArray();
}
