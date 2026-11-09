using System.Globalization;
using Eden.Gameplay.Agents;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Agents;

[Injectable]
internal sealed class MissionContainerHandler(IPilotMissionContainers containers) : ISelectedPilotCallHandler
{
    private const string BindingPrefix = "@missioncontainer:";

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
            !service.StartsWith(BindingPrefix, StringComparison.Ordinal)
            || !long.TryParse(service.AsSpan(BindingPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out id)
        )
        {
            return PilotCallResult.Unsupported;
        }
        if (!MissionContainerRules.IsContainer(id))
        {
            return PilotCallResult.Unsupported;
        }
        if (!context.Flight.CanTarget)
        {
            return new PilotCallResult.Failure("Approach the mission container in a ship outside warp before opening it.");
        }
        try
        {
            context.Flight.Checkpoint();
            if (!context.Flight.CanTarget)
            {
                return new PilotCallResult.Failure("Wait until warp or the system transition has finished before opening the container.");
            }
            var contents = containers.Read(context.Pilot.PilotId, id);
            if (bind)
            {
                return context.Reference(BindingPrefix + id.ToString(CultureInfo.InvariantCulture), PilotBindingLifetime.Location);
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
