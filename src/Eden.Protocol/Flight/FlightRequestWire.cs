using Eden.Gameplay.Flight;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Flight;

internal sealed record FlightRequest(FlightCommand? Command, bool Checkpoint);

internal static class FlightRequestWire
{
    public static FlightRequest Read(string service, string method, object?[] arguments, Dictionary<string, object?>? keywords = null) =>
        new(ReadCommand(service, method, arguments, keywords), service == "beyonce");

    private static FlightCommand? ReadCommand(
        string service,
        string method,
        object?[] arguments,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service == "ship" && method == "Undock" && arguments is [long shipId, ..])
        {
            return new FlightCommand.Undock(shipId);
        }
        if (service == "structureDocking" && method == "Dock" && arguments is [long structureId, long activeShipId])
        {
            return new FlightCommand.Dock(structureId, activeShipId);
        }
        if (service == "structureDocking" && method == "Undock" && arguments is [long _, long undockingShipId, ..])
        {
            return new FlightCommand.Undock(undockingShipId);
        }
        if (service != "beyonce")
        {
            return null;
        }
        return (method, arguments) switch
        {
            ("CmdStargateJump", [long source, long exit, long ship]) => new FlightCommand.Jump(source, exit, ship),
            ("CmdWarpToStuff", [string or byte[], string or byte[]])
                when BlueMarshal.Text(arguments[0]) == "scan" && (keywords?.GetValueOrDefault("minRange") ?? 0L) is double or long =>
                new FlightCommand.WarpToScan(
                    BlueMarshal.Text(arguments[1]),
                    Convert.ToDouble(keywords?.GetValueOrDefault("minRange") ?? 0L)
                ),
            ("CmdWarpToStuff", [string or byte[], long member])
                when BlueMarshal.Text(arguments[0]) == "char" && (keywords?.GetValueOrDefault("minRange") ?? 0L) is double or long =>
                new FlightCommand.WarpToFleetMember(member, Convert.ToDouble(keywords?.GetValueOrDefault("minRange") ?? 0L)),
            ("CmdWarpToStuff", [string or byte[], long bookmark])
                when BlueMarshal.Text(arguments[0]) == "bookmark" && (keywords?.GetValueOrDefault("minRange") ?? 0L) is double or long =>
                new FlightCommand.WarpToBookmark(bookmark, Convert.ToDouble(keywords?.GetValueOrDefault("minRange") ?? 0L)),
            ("CmdWarpToStuff", [string or byte[], long target])
                when BlueMarshal.Text(arguments[0]) is "item" or "launch"
                    && (keywords?.GetValueOrDefault("minRange") ?? 0L) is double or long => new FlightCommand.WarpTo(
                target,
                Convert.ToDouble(keywords?.GetValueOrDefault("minRange") ?? 0L)
            ),
            ("UpdateStateRequest", _) => new FlightCommand.RefreshState(),
            ("CmdGotoDirection", [double x, double y, double z]) => new FlightCommand.Heading(x, y, z),
            ("CmdFollowBall" or "CmdOrbit", [long target, var distance]) when distance is double or long => new FlightCommand.Follow(
                target,
                Convert.ToDouble(distance),
                method == "CmdOrbit"
            ),
            ("CmdSetSpeedFraction", [var speed]) when speed is double or long => new FlightCommand.Speed(Convert.ToDouble(speed)),
            ("CmdStop", _) => new FlightCommand.Stop(),
            ("CmdDock", [long station, long ship]) => new FlightCommand.Dock(station, ship),
            _ => null,
        };
    }
}
