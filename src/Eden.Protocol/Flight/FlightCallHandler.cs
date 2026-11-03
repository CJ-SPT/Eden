using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Flight;

[Injectable]
internal sealed class FlightCallHandler(ISystemObjects systems) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        var command = FlightRequestWire.Read(service, method, args, keywords);
        return command is FlightCommand.Jump jump ? context.Flight.StartJump(jump, systems) : context.Flight.Execute(command);
    }
}
