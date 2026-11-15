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
        if (!bind && service != DeliveriesService)
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
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
    }
}
