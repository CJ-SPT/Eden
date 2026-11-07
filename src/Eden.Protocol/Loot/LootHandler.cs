using System.Globalization;
using Eden.Gameplay.Loot;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Loot;

[Injectable]
internal sealed class LootHandler(IWrecks wrecks, TimeProvider clock) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool bind = service == "invbroker" && method == "GetInventoryFromId" && args is [long, ..];
        long id;
        if (bind)
        {
            id = (long)args[0]!;
        }
        else if (
            !service.StartsWith(WreckWireDefinitions.BindingPrefix, StringComparison.Ordinal)
            || !long.TryParse(
                service.AsSpan(WreckWireDefinitions.BindingPrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out id
            )
        )
        {
            return PilotCallResult.Unsupported;
        }
        if (bind && !context.Flight.VisibleWrecks.Any(w => w.Id == id))
        {
            return PilotCallResult.Unsupported;
        }
        if (!context.Flight.CanTarget)
        {
            return new PilotCallResult.Failure("Approach the wreck in a ship outside warp before opening it.");
        }
        try
        {
            context.Flight.Checkpoint();
            var contents = wrecks.Read(context.Pilot.PilotId, id, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            if (bind)
            {
                return context.Reference(
                    WreckWireDefinitions.BindingPrefix + id.ToString(CultureInfo.InvariantCulture),
                    PilotBindingLifetime.Location
                );
            }
            if (method == "GetSelfInvItem" && args.Length == 0)
            {
                return InventoryRow.From(contents.Container);
            }
            if (method == "List")
            {
                object? flag = args.FirstOrDefault() ?? keywords?.GetValueOrDefault("flag");
                return new BlueList<InventoryRow>(
                    contents.Items.Where(item => flag is null || Equals(flag, (long)item.FlagId)).Select(InventoryRow.From).ToArray()
                );
            }
            return PilotCallResult.Unsupported;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
