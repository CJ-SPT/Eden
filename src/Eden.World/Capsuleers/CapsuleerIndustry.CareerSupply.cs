using Eden.Data.Catalog;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    internal static int PreferredAvailableCareerKit(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        int[] choices,
        int first,
        long now
    )
    {
        int station = pilot.StationId;
        long id = pilot.PilotId;
        if (choices.Length == 1 || station == 0 || Employer(world, id, pilot.CorpId) is null)
        {
            return choices[first];
        }

        var stock = Hangar(world, id, station).Where(item => !item.Singleton).ToLookup(item => item.TypeId);
        long balance = CapsuleerRefitFunding.Applies(world, id) ? long.MaxValue : WalletBalance(world, id);
        // Read the station's existing listings without seeding or refreshing supply just to choose a goal.
        var asks =
            balance > 0 && StationCapabilities.Has(catalog, pilot.OperationId, StationService.Market)
                ? world
                    .Filter<SavedPersonalOrder>(order => order.StationId == station)
                    .Where(order =>
                        !order.Bid
                        && order.Status == PersonalOrderStatus.Open
                        && order.Expires > now
                        && order.Remaining > 0
                        && order.PriceCents > 0
                        && order.PilotId != id
                    )
                    .OrderBy(order => order.PriceCents)
                    .ThenBy(order => order.Id)
                    .AsEnumerable()
                    .ToLookup(order => order.TypeId, order => order.Snapshot())
                : null;
        if (stock.Count == 0 && asks is not { Count: > 0 })
        {
            return choices[first];
        }

        int? affordable = null;
        for (int offset = 0; offset < choices.Length; offset++)
        {
            int type = choices[(first + offset) % choices.Length];
            if ((!stock.Contains(type) && asks?.Contains(type) != true) || CareerKit(catalog, type) is not { } kit)
            {
                continue;
            }
            var missing = MissingParts(kit.Parts(), stock);
            if (missing.Length == 0)
            {
                return type;
            }
            if (affordable is null && asks is not null && Affordable(missing, asks, catalog, balance, UsesPatrolSupport(catalog, type)))
            {
                affordable = type;
            }
        }
        return affordable ?? choices[first];
    }

    private static bool Affordable(
        ReplacementPart[] missing,
        ILookup<int, MarketOrder> asks,
        CatalogDbContext catalog,
        decimal budget,
        bool coverProduction
    )
    {
        foreach (var part in missing)
        {
            long cap = CareerQuoteLimit(catalog, part.TypeId, coverProduction);
            var purchase = QuoteInput(asks[part.TypeId].TakeWhile(order => order.PriceCents <= cap), part.TypeId, part.Quantity);
            if (purchase is null || purchase.Quantity != part.Quantity)
            {
                return false;
            }
            budget -= (decimal)purchase.LimitPriceCents * purchase.Quantity;
            if (budget < 0)
            {
                return false;
            }
        }
        return true;
    }
}
