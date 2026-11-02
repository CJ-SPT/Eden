using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Flight;

[Injectable]
internal sealed class FlightCallHandler : ISelectedPilotCallHandler
{
    public object? Call(PilotCallContext context, string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
        => context.Flight.Call(service, method, args, keywords);
}
