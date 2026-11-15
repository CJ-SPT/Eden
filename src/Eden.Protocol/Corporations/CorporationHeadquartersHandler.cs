using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationHeadquartersHandler(ICorporationHeadquarters headquarters) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "corpStationMgr" || method != "MoveCorpHQHere")
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (args.Length != 0 || keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected headquarters change arguments.");
            }
            headquarters.MoveHere(context.Pilot.PilotId);
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
