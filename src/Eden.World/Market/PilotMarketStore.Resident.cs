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
            if (!Enumerable.Contains(knownTypes, id) || !TestingMarketRules.IsEligible(new(id, type.Published == 1, group)))
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
        foreach (var offer in s_offers.Where(o => o.BuyPriceCents.HasValue && Enumerable.Contains(knownTypes, o.TypeId)))
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

    private void Refresh(WorldAction world, IQueryable<NpcMarketOrder> orders)
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
        var refreshedOrders = orders
            .Where(o =>
                o.Kind == NpcMarketKind.Development
                && !o.Bid
                && !Enumerable.Contains(retiredSkills, o.TypeId)
                && world.Set<MarketStation>().Any(s => s.StationId == o.StationId && s.SeedVersion == TestingMarketRules.SeedVersion)
            )
            .Where(o =>
                o.Remaining != world.Settings.DevelopmentSupply.TestingStock
                || o.PriceCents != world.Settings.DevelopmentSupply.TestingPriceCents
                || o.Issued <= cutoff
            )
            .ToArray();
        foreach (var order in refreshedOrders)
        {
            order.Remaining = world.Settings.DevelopmentSupply.TestingStock;
            order.Entered = world.Settings.DevelopmentSupply.TestingStock;
            order.PriceCents = world.Settings.DevelopmentSupply.TestingPriceCents;
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
            .Set<MarketExecution>()
            .AsNoTracking()
            .Where(t => t.PilotId == pilotId && t.Time >= from && t.Time < until)
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

    internal IReadOnlyList<MarketOrder> SupplyOrders(WorldAction world, CatalogDbContext catalog, int stationId, IReadOnlyList<int> types)
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
                && Enumerable.Contains(types, o.TypeId)
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
                && Enumerable.Contains(types, o.TypeId)
                && o.Status == PersonalOrderStatus.Open
                && o.Expires > Now
            )
            .AsEnumerable()
            .Select(o => o.Snapshot());
        return npc.Concat(personal).OrderBy(o => o.PriceCents).ThenBy(o => o.Issued).ThenBy(o => o.Id).ToArray();
    }
}
