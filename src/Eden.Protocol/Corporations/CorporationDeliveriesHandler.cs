using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationDeliveriesHandler(ICorporationStorage storage) : ISelectedPilotCallHandler
{
    private const string DeliveriesService = "@corporationDeliveries";

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool bind =
            service == "invbroker" && method == "GetInventory" && args is [(long)CorporationStorageRules.MarketDeliveriesContainer, ..];
        bool deliver = service == "invbroker" && method == "DeliverToCorpMember";
        if (!bind && !deliver && service != DeliveriesService)
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (deliver)
            {
                if (
                    keywords is { Count: > 0 }
                    || args
                        is not [
                            long recipient,
                            long station,
                            List<object?> items,
                            var quantity,
                            long corporation,
                            (long)CorporationStorageRules.MarketDeliveriesFlag,
                        ]
                    || recipient <= 0
                    || station is <= 0 or > int.MaxValue
                    || corporation is <= 0 or > int.MaxValue
                    || quantity is not (null or long and > 0 and <= int.MaxValue)
                    || items.Any(value => value is not (long and > 0))
                )
                {
                    throw new InvalidDataException("Choose local Market Deliveries and a corporation member.");
                }
                long actor = context.Pilot.PilotId;
                var changes = storage.DeliverToMember(
                    actor,
                    new(recipient, (int)station, (int)corporation, items.Cast<long>().ToArray(), quantity is long count ? (int)count : null)
                );
                if (recipient == actor)
                {
                    CorporationStorageHandler.NotifyItems(context, changes);
                }
                else
                {
                    foreach (var change in changes)
                    {
                        if (change.Item.OwnerId == corporation)
                        {
                            InventoryChangesWire.NotifyItemChange(context, change);
                        }
                        else if (change.Previous is { } previous)
                        {
                            InventoryChangesWire.NotifyItemChange(context, new(previous with { Quantity = 0 }, previous));
                        }
                    }
                }
                return PilotCallResult.None;
            }
            if (bind)
            {
                if (
                    args.Length > 2
                    || args.Length == 2 && args[1] is not null
                    || keywords is { Count: > 0 }
                        && (keywords.Count != 1 || !Equals(keywords.GetValueOrDefault("stationid"), context.Pilot.DockedLocationId))
                )
                {
                    throw new InvalidDataException("Invalid corporation deliveries binding.");
                }
                _ = storage.Deliveries(context.Pilot.PilotId);
                return context.Reference(DeliveriesService, PilotBindingLifetime.Location);
            }
            if (
                method == "List"
                && (args is [] or [null] or [(long)CorporationStorageRules.MarketDeliveriesFlag])
                && (
                    keywords is null or { Count: 0 }
                    || args.Length == 0
                        && keywords.Count == 1
                        && keywords.TryGetValue("flag", out var flag)
                        && flag is null or (long)CorporationStorageRules.MarketDeliveriesFlag
                )
            )
            {
                return new BlueList<InventoryRow>(storage.Deliveries(context.Pilot.PilotId).Select(InventoryRow.From).ToArray());
            }
            throw new InvalidDataException("Invalid corporation deliveries request.");
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("The requested delivery exceeds the supported limit.");
        }
    }
}
