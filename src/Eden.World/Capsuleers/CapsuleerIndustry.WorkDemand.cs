using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Industry;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    internal static int ReadyHuntReplacements(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, Ship? candidate = null)
    {
        if (pilot.Position is not null || pilot.StationId == 0)
        {
            return 0;
        }
        var stock = Hangar(world, pilot.PilotId, pilot.StationId).Where(item => !item.Singleton).ToLookup(item => item.TypeId);
        if (!stock.Contains(candidate?.TypeId ?? pilot.ShipTypeId) || HuntKit(world, catalog, pilot, candidate) is not { } parts)
        {
            return 0;
        }
        return CompleteKits(parts, stock);
    }

    internal bool TryReplenishHuntReserve(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        long now,
        Ship? candidate = null
    )
    {
        long id = pilot.PilotId;
        int station = pilot.StationId;
        long balance = WalletBalance(world, id);
        if (
            balance <= 0
            || pilot.Position is not null
            || station == 0
            || !StationCapabilities.Has(catalog, pilot.OperationId, StationService.Market)
            || HuntKit(world, catalog, pilot, candidate) is not { } parts
        )
        {
            return false;
        }
        var stock = Hangar(world, id, station).Where(item => !item.Singleton).ToLookup(item => item.TypeId);
        int ready = CompleteKits(parts, stock);
        if (ready >= CapsuleerWorkRules.MinimumHuntingReplacements)
        {
            return false;
        }
        // Keep enough capital for repairs, consumables and normal work after replenishing a spare.
        decimal capital = parts.Sum(part => (decimal)ReferencePrice(catalog, part.TypeId) * part.Quantity);
        decimal budget = Math.Min(balance - capital, balance / RattingRules.BudgetDivisor);
        if (capital <= 0 || budget <= 0)
        {
            return false;
        }
        var organization = CapsuleerCorporations.Organization(world, id);
        if (organization.HuntReserveJobId.HasValue)
        {
            // An installed job occupies this spare's budget until its output can be delivered.
            return false;
        }
        var missing = MissingParts(parts.Select(part => part with { Quantity = checked(part.Quantity * (ready + 1)) }).ToArray(), stock);
        if (
            missing.Any(part => !IsMarketSupply(catalog, part.TypeId))
            || world
                .Filter<SavedPersonalOrder>(order => order.PilotId == id)
                .Where(order => order.StationId == station && order.Bid && order.Status == PersonalOrderStatus.Open && order.Expires > now)
                .Select(order => order.TypeId)
                .AsEnumerable()
                .Any(type => parts.Any(part => part.TypeId == type))
        )
        {
            return false;
        }
        bool coverProduction = UsesPatrolSupport(catalog, candidate?.TypeId ?? pilot.ShipTypeId);
        if (QuoteCareerKit(world, catalog, id, station, missing, budget, coverProduction) is not { } purchases)
        {
            if (TryManufactureHuntReserve(world, catalog, pilot, missing, budget, organization))
            {
                return false;
            }
            foreach (var part in missing)
            {
                if (PlaceCareerBid(world, catalog, pilot, [part], (long)budget, now, coverProduction))
                {
                    return true;
                }
            }
            return false;
        }
        var bought = market.ExecuteBuy(world, catalog, id, station, purchases, 0);
        if (bought.Trades.Sum(trade => (long)trade.Quantity) != purchases.Sum(purchase => (long)purchase.Quantity))
        {
            throw new InvalidDataException("Hunt reserve offers changed before purchase.");
        }
        return true;
    }

    internal static bool HasWorkDemand(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        long now,
        bool requireProductBlueprint = false
    )
    {
        long id = pilot.PilotId;
        int station = pilot.StationId;
        if (
            pilot.Position is not null
            || station == 0
            || !StationCapabilities.Has(catalog, pilot.OperationId, StationService.Market)
            || !StationCapabilities.Has(catalog, pilot.OperationId, StationService.Manufacturing)
        )
        {
            return false;
        }
        if (Employer(world, id, pilot.CorpId) is not { } employer)
        {
            return false;
        }
        int corporation = employer.Id;
        var jobs = world
            .StationInstalledIndustryJobs(station)
            .Where(job => job.PilotId == id || job.OutputCorporationId == corporation)
            .ToArray();
        if (jobs.Any(job => job.PilotId == id && PilotIndustryStore.Ready(world, world.Set<IndustryJob>().Find(job.Id)!, now)))
        {
            return true;
        }
        var orders = world
            .StationPersonalOrders(station)
            .Where(order => order.Status == PersonalOrderStatus.Open && order.Expires > now && order.Remaining > 0 && order.PriceCents > 0)
            .ToArray();
        var customerBids = orders
            .Where(order => order.Bid && order.EscrowCents >= order.PriceCents && order.PilotId != id && order.CorporationId != corporation)
            .ToArray();
        bool reserveEligible = employer.BalanceCents > 0 && employer.StationId == station;
        if (customerBids.Length == 0 && !reserveEligible)
        {
            return false;
        }
        var stock = DepotStacks(world, corporation, station);
        var onHand = stock.ToLookup(item => item.TypeId, item => (long)item.Quantity);
        var pending = jobs.Where(job =>
                job.SuspendedAt is null && job.Activity == IndustryActivity.Manufacturing && (job.OutputLocationId ?? station) == station
            )
            .ToLookup(job => job.ProductTypeId, job => (long)job.OutputQuantity);
        var asks = orders.Where(order => !order.Bid).ToLookup(order => order.TypeId);
        var recipes = catalog.Definitions.GlobalDefinition(s_recipesByProduct);
        var members = world
            .Filter<Pilot>(member => member.CorporationId == corporation)
            .Take(CapsuleerCorporationRules.TargetMembers)
            .ToArray();
        var assigned = customerBids.Length > 0 ? AssignedFactoryOrders(world, pilot, now, members) : [];
        var blueprints = ManufacturingCatalog.Recipes(catalog);
        var products = Hangar(world, id, station)
            .Where(item => item.BlueprintRuns != 0)
            .Concat(
                world
                    .Filter<OwnedItem>(item => item.OwnerId == corporation)
                    .Where(item =>
                        item.LocationId == station
                        && item.FlagId == CorporationStorageRules.MarketDeliveriesFlag
                        && item.Singleton
                        && item.BlueprintRuns > 0
                    )
            )
            .Select(item => blueprints.GetValueOrDefault(item.TypeId)?.ProductTypeId)
            .OfType<int>()
            .ToHashSet();
        double security = ManufacturingCatalog.Security(catalog, pilot.SystemId);
        foreach (var bids in customerBids.Where(order => !assigned.Contains(order.Id)).GroupBy(order => order.TypeId))
        {
            int type = bids.Key;
            if (!recipes.Contains(type) || !ManufacturingCatalog.Allowed(catalog, type, security))
            {
                continue;
            }
            long demand = bids.Sum(order => Math.Min(order.Remaining, order.EscrowCents / order.PriceCents));
            long buyPrice = bids.Min(order => order.PriceCents);
            long listed = asks[type].Where(order => order.PriceCents <= buyPrice).Sum(order => (long)order.Remaining);
            if (demand > listed + onHand[type].Sum() + pending[type].Sum() && (!requireProductBlueprint || products.Contains(type)))
            {
                return true;
            }
            if (
                demand > listed
                && onHand.Contains(type)
                && world
                    .Filter<IndustryJob>(job => job.OutputCorporationId == corporation)
                    .Any(job =>
                        job.StationId == station
                        && job.ProductTypeId == type
                        && job.Status == IndustryJobStatus.Delivered
                        && job.OutputFlagId == CorporationStorageRules.MarketDeliveriesFlag
                    )
            )
            {
                return true;
            }
        }

        if (!reserveEligible || products.Count == 0)
        {
            return false;
        }
        var procurement = orders
            .Where(order => order.Bid && order.CorporationId == corporation && order.EscrowCents >= order.PriceCents)
            .ToLookup(order => order.TypeId, order => Math.Min(order.Remaining, order.EscrowCents / order.PriceCents));
        return FleetStockTargets(world, catalog, station, headquarters: true, members)
            .Any(part =>
                products.Contains(part.TypeId)
                && ManufacturingCatalog.Allowed(catalog, part.TypeId, security)
                && part.Quantity > onHand[part.TypeId].Sum() + pending[part.TypeId].Sum() + procurement[part.TypeId].Sum()
            );
    }

    private static int CompleteKits(ReplacementPart[] parts, ILookup<int, OwnedItem> stock) =>
        (int)Math.Min(int.MaxValue, parts.Min(part => stock[part.TypeId].Sum(item => (long)item.Quantity) / part.Quantity));

    private static ReplacementPart[]? HuntKit(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, Ship? candidate = null)
    {
        var ship = world.Ships.Find(candidate?.Id ?? pilot.ShipId);
        if (
            ship is null
            || ship.OwnerId != pilot.PilotId
            || ship.Position is not null
            || ship.StoredInId.HasValue
            || ship.DockedLocationId != pilot.StationId
            || !CapsuleerPatrolFit.CombatHull(catalog, ship.TypeId)
        )
        {
            return null;
        }
        var kit = ActiveKit(
            world,
            catalog,
            new Pilot
            {
                Id = pilot.PilotId,
                ActiveShipId = ship.Id,
                ActiveShip = ship,
            }
        );
        var parts = kit.Parts();
        return kit.OnlineSlots.Count > 0 && CanReplaceKit(world, catalog, pilot.PilotId, CapsuleerCareer.Hauling, kit, parts)
            ? parts
            : null;
    }
}
