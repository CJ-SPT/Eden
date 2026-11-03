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
    ) => flight.Execute(FlightRequestWire.Read(service, method, arguments, keywords));

    public static PilotCallResult StartJump(this FlightSession flight, object?[] arguments, ISystemObjects systems) =>
        FlightRequestWire.Read("beyonce", "CmdStargateJump", arguments) is FlightCommand.Jump jump
            ? flight.StartJump(jump, systems)
            : flight.Execute(new FlightCommand.Unsupported(true));
}
