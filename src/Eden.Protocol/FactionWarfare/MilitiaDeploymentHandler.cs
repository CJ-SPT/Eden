using Eden.Gameplay.FactionWarfare;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.FactionWarfare;

[Injectable]
internal sealed class MilitiaDeploymentHandler(IMilitiaObjectiveDeployment deployments, TimeProvider clock) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if ((service, method) != ("ship", "Drop"))
        {
            return PilotCallResult.Unsupported;
        }
        if (
            args is not [List<object?> items, long owner, bool _]
            || items.Count is < 1 or > 3
            || owner != context.Pilot.PilotId
            || keywords is { Count: > 0 }
        )
        {
            return new PilotCallResult.Failure("Select militia cargo items to launch for yourself.");
        }
        List<long> ids = [];
        foreach (var item in items)
        {
            if (item is not object?[] { Length: 2 } entry || entry is not [long id and > 0, 1L])
            {
                return new PilotCallResult.Failure("Launch one unit per selected militia cargo stack.");
            }
            ids.Add(id);
        }
        if (!context.Flight.CanTarget)
        {
            return new PilotCallResult.Failure("Launch militia deployables from your active ship outside warp.");
        }
        try
        {
            context.Flight.Checkpoint();
            var deployed = deployments.Deploy(
                context.Pilot.PilotId,
                context.Pilot.ShipId,
                ids,
                clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()
            );
            foreach (var row in deployed)
            {
                InventoryChangesWire.NotifyItemChange(context, row.Change);
            }
            return new DeploymentReply(deployed);
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private sealed record DeploymentReply(IReadOnlyList<MilitiaDeployment> Deployments) : BlueModel
    {
        public override object ToBlue() => Deployments.ToDictionary(row => row.ItemId, row => (object?)new BlueList<long>([row.ObjectId]));
    }
}
