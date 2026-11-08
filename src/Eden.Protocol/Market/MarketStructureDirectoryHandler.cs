using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Market;

[Injectable]
internal sealed class MarketStructureDirectoryHandler : ISelectedPilotCallHandler
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
        // Build 3662168 StructureDirectory.GetMarketDockableLocationsInRegion
        // calls iteritems() and filters each structure's services. Eden has no
        // player structures; NPC stations remain in the client's static data.
        return BlueEmpty.Dictionary;
    }
}
