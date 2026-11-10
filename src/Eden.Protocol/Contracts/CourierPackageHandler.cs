using System.Globalization;
using Eden.Gameplay.Contracts;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

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
        if (
            !service.StartsWith(InventoryPrefix, StringComparison.Ordinal)
            || method is not ("BreakPlasticWrap" or "GetSelfInvItem" or "GetCapacity")
        )
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
            if (contracts.Package(context.Pilot.PilotId, itemId) is not { } package)
            {
                return new PilotCallResult.Failure("This courier package is unavailable.");
            }
            return method == "GetCapacity" ? new CourierPackageCapacityReply(package.Item.Volume) : InventoryRow.From(package.Item);
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record CourierPackageCapacityReply(double Volume) : BlueModel
{
    // Build 3662168 inventorycommon.util.GetVolumeForPlasticItem reads GetCapacity().used.
    // Escrow holds the manifest; the saved wrap volume is the sealed container's full capacity.
    public override object ToBlue() =>
        new BlueInstance("utillib.KeyVal", new Dictionary<string, object?> { ["capacity"] = Volume, ["used"] = Volume });
}
