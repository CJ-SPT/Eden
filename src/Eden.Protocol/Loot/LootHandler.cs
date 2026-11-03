using System.Globalization;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Loot;

[Injectable]
internal sealed class LootHandler(IWrecks wrecks, TimeProvider clock) : ISelectedPilotCallHandler
{
    public object? Call(
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
            !service.StartsWith(WreckDefinitions.BindingPrefix, StringComparison.Ordinal)
            || !long.TryParse(
                service.AsSpan(WreckDefinitions.BindingPrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out id
            )
        )
        {
            return PilotCallRouter.Unsupported;
        }
        if (bind && !context.Flight.VisibleWrecks.Any(w => w.Id == id))
        {
            return PilotCallRouter.Unsupported;
        }
        if (!context.Flight.CanTarget)
        {
            return new PilotCallRouter.Error("Approach the wreck in a ship outside warp before opening it.");
        }
        try
        {
            context.Flight.Checkpoint();
            var contents = wrecks.Read(context.Pilot.PilotId, id, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            if (bind)
            {
                return context.Reference(WreckDefinitions.BindingPrefix + id.ToString(CultureInfo.InvariantCulture));
            }
            if (method == "GetSelfInvItem" && args.Length == 0)
            {
                return InventoryHandler.Item(contents.Container);
            }
            if (method == "List")
            {
                object? flag = args.FirstOrDefault() ?? keywords?.GetValueOrDefault("flag");
                return contents
                    .Items.Where(i => flag is null || Equals(flag, (long)i.FlagId))
                    .Select(i => (object?)InventoryHandler.Item(i))
                    .ToList();
            }
            return PilotCallRouter.Unsupported;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallRouter.Error(error.Message);
        }
    }
}
