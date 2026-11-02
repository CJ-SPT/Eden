using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Flight;

[Injectable]
internal sealed class FlightCallHandler(ISystemObjects systems) : ISelectedPilotCallHandler
{
    public object? Call(PilotCallContext context, string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
        => service == "beyonce" && method == "CmdStargateJump"
            ? context.Flight.StartJump(args, systems) : context.Flight.Call(service, method, args, keywords);
}
