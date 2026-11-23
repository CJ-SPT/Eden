using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Industry;
using Eden.Data.World.Market;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Industry;
using Eden.World.Market;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;

namespace Eden.World.Capsuleers;

internal readonly record struct CareerShipTurn(bool Boarded = false, bool MarketChanged = false);

internal sealed partial class CapsuleerIndustry
{
    private sealed record CareerProductionBid(long PriceCents, int BatchSize);

    private const decimal CareerProductionMargin = 1.25m;
    private static readonly CatalogDefinition<ReplacementKit?> s_careerKits = new("career-ship-kits", LoadCareerKit);
    private static readonly CatalogDefinition<CareerProductionBid?> s_careerProductionBids = new(
        "career-production-bids",
        LoadCareerProductionBid
    );

    internal static ReplacementKit? CareerKit(CatalogDbContext catalog, int type) => catalog.Definition(s_careerKits, type);

    private static CareerProductionBid? LoadCareerProductionBid(CatalogDbContext catalog, int type)
    {
        var recipes = catalog.Definitions.GlobalDefinition(s_recipesByProduct);
        var best = recipes[type]
            .Select(recipe =>
            {
                decimal value = 0;
                foreach (var (input, quantity) in IndustryMaterials.Calculate(recipe.Materials, 1))
                {
                    long price = ReferencePrice(catalog, input);
                    if (price <= 0)
                    {
                        return (UnitCost: 0m, recipe.OutputQuantity);
                    }
                    value += (decimal)price * quantity;
                }
                return (UnitCost: value / recipe.OutputQuantity, recipe.OutputQuantity);
            })
            .Where(quote => quote.UnitCost > 0)
            .OrderBy(quote => quote.UnitCost)
            .FirstOrDefault();
        if (best.OutputQuantity == 0)
        {
            return null;
        }
        // Larger hulls and their modules can cost more to make than the captured finished-goods quote.
        decimal gross = Math.Max(ReferencePrice(catalog, type), decimal.Ceiling(best.UnitCost * CareerProductionMargin));
        long price = (long)Math.Min(MarketDefinitions.MaximumOrderPrice * MarketDefinitions.CentsPerIsk, gross);
        return new(price, best.OutputQuantity);
    }

    private static bool UsesPatrolSupport(CatalogDbContext catalog, int hull) =>
        CapsuleerDefenseRules.PatrolWeaponSize((HullClass)(catalog.FittingType(hull)?.GroupId ?? 0))
            is ChargeSize.Medium
                or ChargeSize.Large;

    private static long CareerQuoteLimit(CatalogDbContext catalog, int type, bool coverProduction) =>
        Math.Max(
            CapsuleerReplacementRules.LimitPrice(ReferencePrice(catalog, type)),
            coverProduction ? catalog.Definition(s_careerProductionBids, type)?.PriceCents ?? 0 : 0
        );

    private static ReplacementKit? LoadCareerKit(CatalogDbContext catalog, int type)
    {
        var hull = (HullClass)(catalog.FittingType(type)?.GroupId ?? 0);
        const int high = (int)InventoryFlag.HighSlot0;
        var profession = CapsuleerCareerTraining.Profession(catalog, type, hull);
        ReplacementKit? kit = null;
        if (
            profession == CapsuleerProfession.Mining
            && hull is HullClass.Frigate or HullClass.ExpeditionFrigate or HullClass.MiningBarge or HullClass.Exhumer
        )
        {
            int laser = (int)CapsuleerMiningRules.Laser((CapsuleerMiningHull)type);
            kit = new(type, [new(high, laser)], new HashSet<int> { high }, [], []);
        }
        else if (profession == CapsuleerProfession.Hauling)
        {
            int drive = (int)StarterMarketType.Afterburner;
            int mid = (int)InventoryFlag.MediumSlot0;
            var slots =
                catalog.FittingType(drive) is not null
                && catalog.FittingAttributes(type).GetValueOrDefault(DogmaAttributeId.MediumSlots) > 0
                && WithinBaseFittingResources(catalog, type, [new(mid, drive)])
                    ? new FittingSlot[] { new(mid, drive) }
                    : [];
            kit = new(type, slots, slots.Select(slot => slot.FlagId).ToHashSet(), [], []);
        }
        else if (
            profession == CapsuleerProfession.Combat
            && hull != HullClass.StrategicCruiser
            && CapsuleerPatrolFit.CareerFit(catalog, type) is { } fit
        )
        {
            var weapons = fit.Equipment.Where(item => InventoryDefinitions.IsHighSlot(item.Flag)).ToLookup(item => item.Flag);
            var highSlots = weapons
                .Select(slot => new FittingSlot(
                    slot.Key,
                    slot.Single(item => catalog.FittingType(item.TypeId)?.CategoryId == (int)InventoryCategory.Module).TypeId
                ))
                .ToArray();
            var magazines = highSlots
                .Select(slot =>
                    Magazine(
                        catalog,
                        slot.FlagId,
                        slot.TypeId,
                        weapons[slot.FlagId]
                            .Single(item => catalog.FittingType(item.TypeId)?.CategoryId == (int)InventoryCategory.Charge)
                            .TypeId
                    )
                )
                .OfType<ReplacementMagazine>()
                .ToArray();
            if (highSlots.Length > 0 && magazines.Length == highSlots.Length)
            {
                // The published starter Merlin keeps its civilian-to-railgun purchasing plan. Larger ships
                // acquire the propulsion, tackle and tank from their catalog-validated patrol fit as well.
                var fitting = !UsesPatrolSupport(catalog, type)
                    ? highSlots
                    :
                    [
                        .. highSlots,
                        .. fit
                            .Equipment.Where(item =>
                                InventoryDefinitions.IsFittedSlot(item.Flag)
                                && !InventoryDefinitions.IsHighSlot(item.Flag)
                                && catalog.FittingType(item.TypeId)?.CategoryId == (int)InventoryCategory.Module
                            )
                            .Select(item => new FittingSlot(item.Flag, item.TypeId)),
                    ];
                if (WithinBaseFittingResources(catalog, type, fitting))
                {
                    kit = new(type, fitting, fitting.Select(slot => slot.FlagId).ToHashSet(), [], magazines);
                }
            }
        }
        return kit is not null && SupportedKit(catalog, kit, kit.Parts()) ? kit : null;
    }

    internal CareerShipTurn AdvanceCareerShip(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        CapsuleerCareerPlan? plan,
        long now
    )
    {
        long id = pilot.PilotId;
        int station = pilot.StationId;
        if (pilot.Position is not null || station <= 0)
        {
            return default;
        }
        if (plan is not { ShipTypeId: > 0 } || plan.ShipTypeId == pilot.ShipTypeId || CareerKit(catalog, plan.ShipTypeId) is not { } kit)
        {
            return default;
        }
        var parts = kit.Parts();
        bool coverProduction = plan.Profession == CapsuleerProfession.Combat && UsesPatrolSupport(catalog, kit.HullTypeId);
        if (
            !CanReplaceKit(
                world,
                catalog,
                id,
                plan.Profession == CapsuleerProfession.Mining ? CapsuleerCareer.Mining : CapsuleerCareer.Hauling,
                kit,
                parts
            )
        )
        {
            return default;
        }

        var hangar = Hangar(world, id, station);
        var missing = MissingParts(parts, hangar.ToLookup(item => item.TypeId));
        var bids = coverProduction ? LocalCareerBids(world, id, station, parts, now) : [];
        if (missing.Length == 0)
        {
            CancelCareerBids(world, catalog, bids);
            BoardKit(world, catalog, pilot, kit, now);
            return new(Boarded: true, MarketChanged: bids.Length > 0);
        }
        if (!StationCapabilities.Has(catalog, pilot.OperationId, StationService.Market))
        {
            return default;
        }
        long balance = WalletBalance(world, id);
        decimal spending = CapsuleerRefitFunding.Applies(world, id) ? long.MaxValue : balance + bids.Sum(bid => (decimal)bid.EscrowCents);
        if (spending > 0 && QuoteCareerKit(world, catalog, id, station, missing, spending, coverProduction) is { } purchases)
        {
            CancelCareerBids(world, catalog, bids);
            market.BuyBotRefitSupplies(world, catalog, id, station, purchases);
            if (MissingParts(parts, Hangar(world, id, station).ToLookup(item => item.TypeId)).Length == 0)
            {
                BoardKit(world, catalog, pilot, kit, now);
                return new(Boarded: true, MarketChanged: true);
            }
            return new(MarketChanged: true);
        }
        return new(MarketChanged: PlaceCareerBid(world, catalog, pilot, missing, balance, now, coverProduction));
    }

    private MarketPurchase[]? QuoteCareerKit(
        EconomyTransaction world,
        CatalogDbContext catalog,
        long pilot,
        int station,
        ReplacementPart[] missing,
        decimal budget,
        bool coverProduction = false
    )
    {
        var offers = market
            .SupplyOrders(world, catalog, station, missing.Select(part => part.TypeId).ToArray(), pilot)
            .ToLookup(order => order.TypeId);
        List<MarketPurchase> purchases = [];
        foreach (var part in missing)
        {
            long cap = CareerQuoteLimit(catalog, part.TypeId, coverProduction);
            if (
                QuoteInput(offers[part.TypeId].Where(order => order.PriceCents <= cap), part.TypeId, part.Quantity) is not { } purchase
                || purchase.Quantity != part.Quantity
            )
            {
                return null;
            }
            budget -= (decimal)purchase.LimitPriceCents * purchase.Quantity;
            if (budget < 0)
            {
                return null;
            }
            purchases.Add(purchase);
        }
        return purchases.ToArray();
    }

    private static SavedPersonalOrder[] LocalCareerBids(
        EconomyTransaction world,
        long pilot,
        int station,
        ReplacementPart[] missing,
        long now
    ) =>
        world
            .Filter<SavedPersonalOrder>(row => row.PilotId == pilot)
            .AsEnumerable()
            .Where(row =>
                row.CorporationId is null
                && row.Bid
                && row.StationId == station
                && row.Status == PersonalOrderStatus.Open
                && row.Expires > now
                && missing.Any(part => part.TypeId == row.TypeId)
            )
            .ToArray();

    private void CancelCareerBids(EconomyTransaction world, CatalogDbContext catalog, SavedPersonalOrder[] bids)
    {
        foreach (var bid in bids)
        {
            market.CancelPersonalSupplyBid(world, catalog, bid);
        }
    }

    private bool PlaceCareerBid(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        ReplacementPart[] missing,
        long balance,
        long now,
        bool coverProduction = false
    )
    {
        long id = pilot.PilotId;
        int station = pilot.StationId;
        var part = missing[0];
        if (
            balance <= 0
            || world
                .Filter<SavedPersonalOrder>(order => order.PilotId == id)
                .Any(order => order.Bid && order.TypeId == part.TypeId && order.Status == PersonalOrderStatus.Open && order.Expires > now)
            || !PilotMarketStore.HasSupplyListingSlot(world, catalog, id)
        )
        {
            return false;
        }
        var production = coverProduction ? catalog.Definition(s_careerProductionBids, part.TypeId) : null;
        long price = production?.PriceCents ?? ReferencePrice(catalog, part.TypeId);
        int batch = production?.BatchSize ?? 1;
        int quantity = checked(((part.Quantity - 1) / batch + 1) * batch);
        if (
            price <= 0
            || (decimal)price * quantity > balance
            || PilotMarketStore.SupplyBrokerFee(world, catalog, id, station, price, quantity) > balance - price * quantity
        )
        {
            return false;
        }
        market.ExecuteBuy(world, catalog, id, station, [new(part.TypeId, price, quantity)], CapsuleerReplacementRules.OrderDays);
        return true;
    }
}
