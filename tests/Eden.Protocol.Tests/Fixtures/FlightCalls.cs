using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Tests.Fixtures;

internal static class FlightCalls
{
    public static PilotCallResult Call(
        this FlightSession flight,
        string service,
        string method,
        object?[] arguments,
        Dictionary<string, object?>? keywords = null
    )
    {
        var request = FlightRequestWire.Read(service, method, arguments, keywords);
        if (request.Command is { } command)
        {
            return flight.Execute(command);
        }
        if (request.Checkpoint)
        {
            flight.Checkpoint();
        }
        return PilotCallResult.Unsupported;
    }

    public static PilotCallResult StartJump(this FlightSession flight, object?[] arguments, ISystemObjects systems) =>
        FlightRequestWire.Read("beyonce", "CmdStargateJump", arguments).Command is FlightCommand.Jump jump
            ? flight.StartJump(jump, systems)
            : flight.Call("beyonce", "CmdStargateJump", arguments);
}
