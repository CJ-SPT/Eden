using System.Globalization;
using Eden.Gameplay.Contracts;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Contracts;

[Injectable]
internal sealed class CourierPackageHandler(IPilotContracts contracts) : ISelectedPilotCallHandler
{
    internal const string InventoryPrefix = "@courier:";

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (!service.StartsWith(InventoryPrefix, StringComparison.Ordinal) || method is not ("BreakPlasticWrap" or "GetSelfInvItem"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (
                args.Length != 0
                || keywords is { Count: > 0 }
                || !long.TryParse(service.AsSpan(InventoryPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long itemId)
            )
            {
                throw new InvalidDataException("Invalid courier package request.");
            }
            if (method == "BreakPlasticWrap")
            {
                contracts.BreakPackage(context.Pilot.PilotId, itemId);
                return PilotCallResult.None;
            }
            return contracts.Package(context.Pilot.PilotId, itemId) is { } package
                ? InventoryRow.From(package.Item)
                : new PilotCallResult.Failure("This courier package is unavailable.");
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
