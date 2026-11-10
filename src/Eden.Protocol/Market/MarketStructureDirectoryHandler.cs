using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Structures;

namespace Eden.Protocol.Market;

[Injectable]
internal sealed class MarketStructureDirectoryHandler(IPilotStructures structures) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "structureDirectory" || method != "GetMyCharacterStructures")
        {
            return PilotCallResult.Unsupported;
        }
        if (args.Length != 0 || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("The character structure directory does not accept arguments.");
        }
        return new StructureDirectoryReply(
            structures
                .List(context.Pilot.PilotId)
                .Where(s => structures.HasService(context.Pilot.PilotId, s.Id, StructureService.Docking))
                .Select(s => new StructureInfoReply(s, true, NativeInstance: false))
                .ToArray()
        );
    }
}
