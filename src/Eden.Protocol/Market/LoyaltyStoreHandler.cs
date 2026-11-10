using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Corporations;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Market;

[Injectable(InjectionType.Scoped)]
internal sealed class LoyaltyStoreHandler(IPilotLoyaltyStore store, LoyaltyWalletHandler loyalty, CorporationWalletHandler corporateWallet)
    : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool check =
            service == "corpStationMgr" && method == "DoStandingCheckForStationService" && args is [(long)StationService.LoyaltyStore];
        bool exchange = service == "LPSvc" && method == "ExchangeConcordLP";
        if (service != "LPStoreMgr" && !check && !exchange)
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unsupported loyalty store keyword arguments.");
            }
            if (check)
            {
                store.CheckStation(context.Pilot.PilotId, context.Pilot.NpcStationId);
                return PilotCallResult.None;
            }
            if (exchange)
            {
                if (args is not [long target, long points] || target is <= 0 or > int.MaxValue || points <= 0)
                {
                    return new PilotCallResult.Failure("Choose an LP issuer and a positive exchange amount.");
                }
                loyalty.Enter(context);
                var exchanged = store.ExchangeConcord(context.Pilot.PilotId, (int)target, points);
                loyalty.Notify(context, LoyaltyStoreRules.ConcordCorporation, exchanged.SourceBefore, exchanged.SourceAfter);
                loyalty.Notify(context, (int)target, exchanged.DestinationBefore, exchanged.DestinationAfter);
                return PilotCallResult.None;
            }
            switch (method)
            {
                case "GetAvailableOffersFromCorp" when args is [long corporation] && corporation is > 0 and <= int.MaxValue:
                    return new BlueList<LoyaltyOfferReply>(
                        store.Offers(context.Pilot.PilotId, (int)corporation).Select(offer => new LoyaltyOfferReply(offer)).ToArray()
                    );
                case "TakeOfferForCharacter"
                or "TakeOfferForCorporation"
                    when args is [long corporation, long offer, long count]
                        && corporation is > 0 and <= int.MaxValue
                        && offer is > 0 and <= int.MaxValue
                        && count is > 0 and <= LoyaltyStoreRules.MaximumOffersPerPurchase:
                    loyalty.Enter(context);
                    corporateWallet.Enter(context);
                    var purchase = store.Purchase(
                        context.Pilot.PilotId,
                        (int)corporation,
                        (int)offer,
                        (int)count,
                        method == "TakeOfferForCorporation"
                    );
                    foreach (var change in purchase.Changes)
                    {
                        InventoryChangesWire.NotifyItemChange(context, change);
                    }
                    if (purchase.CorporationId is int owner && purchase.WalletKey is int key)
                    {
                        corporateWallet.Notify(context, owner, key, purchase.BalanceCents);
                        loyalty.NotifyCorporation(context, (int)corporation, purchase.PointsAfter);
                    }
                    else
                    {
                        context.Notifications.Add(
                            new AccountBalanceChanged(context.Pilot.PilotId, (double)purchase.BalanceCents / MarketDefinitions.CentsPerIsk)
                        );
                        loyalty.Notify(context, (int)corporation, purchase.PointsBefore, purchase.PointsAfter);
                    }
                    return true;
                default:
                    return new PilotCallResult.Failure("Invalid loyalty store request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("The purchase exceeds the inventory limit.");
        }
    }
}

internal sealed record LoyaltyOfferReply(LoyaltyOffer Offer) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["offerID"] = Offer.Id,
                ["corpID"] = Offer.CorporationId,
                ["typeID"] = Offer.TypeId,
                ["qty"] = Offer.Quantity,
                ["lpCost"] = Offer.LpCost,
                ["iskCost"] = (double)Offer.IskCents / MarketDefinitions.CentsPerIsk,
                ["akCost"] = 0,
                ["reqItems"] = Offer.RequiredItems.Select(item => (object?)new object?[] { item.TypeId, item.Quantity }).ToList(),
                ["requiredStandings"] = null,
            }
        );
}
