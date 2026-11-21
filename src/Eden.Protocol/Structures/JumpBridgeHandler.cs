using Eden.Gameplay.Flight;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Structures;

[Injectable]
internal sealed class JumpBridgeHandler(IPilotJumpBridges bridges, ISystemObjects systems) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool component =
            (service, method) == ("beyonce", "CallComponentFromClient")
            && args.Length >= 2
            && args[1] is string or byte[]
            && BlueMarshal.Text(args[1]) == "structureJumpBridge";
        if (!component && service != "structureJumpBridgeMgr" && (service, method) != ("structureDirectory", "GetNearbyJumpBridges"))
        {
            return PilotCallResult.Unsupported;
        }
        if (keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Unexpected jump bridge arguments.");
        }
        try
        {
            var pilot = context.Pilot;
            return (service, method, args) switch
            {
                ("structureDirectory", "GetNearbyJumpBridges", []) => new BlueList<JumpBridgeEndpointReply>(
                    bridges.Nearby(pilot.SystemId).Select(row => new JumpBridgeEndpointReply(row)).ToArray()
                ),
                ("structureJumpBridgeMgr", "GetJbStructureDestination", [long id]) when id > 0 => bridges.Destination(id) is int destination
                    ? new JumpBridgeDestinationReply(destination)
                    : PilotCallResult.None,
                ("structureJumpBridgeMgr", "GetLinkedStructure", [long id]) when id > 0 => bridges.Linked(id) is { } linked
                    ? new JumpBridgeLinkedReply(linked)
                    : PilotCallResult.None,
                ("structureJumpBridgeMgr", "CmdJumpThroughStructureStargate", [long source]) when source > 0 => Jump(context, source),
                ("beyonce", "CallComponentFromClient", [long id, _, string or byte[]])
                    when id > 0 && BlueMarshal.Text(args[2]) == "GetJumpBridgeInfo" => bridges.Info(pilot.PilotId, pilot.ShipId, id)
                    is { } info
                    ? new JumpBridgeInfoReply(info)
                    : PilotCallResult.None,
                _ => new PilotCallResult.Failure("Choose a valid Ansiblex jump bridge."),
            };
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private PilotCallResult Jump(PilotCallContext context, long source)
    {
        context.Flight.StartJumpBridge(bridges, source, systems);
        // The native menu and autopilot check the return value before clearing the waypoint.
        return true;
    }
}

internal sealed record JumpBridgeEndpointReply(JumpBridgeEndpoint Endpoint) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "util.KeyVal",
            new Dictionary<string, object?>
            {
                ["structureID"] = Endpoint.Id,
                ["solarSystemID"] = Endpoint.SystemId,
                ["typeID"] = JumpBridgeRules.Ansiblex,
                ["ownerID"] = Endpoint.OwnerId,
                ["name"] = Endpoint.Name,
                ["destinationSolarsystemID"] = Endpoint.DestinationSystemId,
            }
        );
}

internal sealed record JumpBridgeNetworkReply(JumpBridgeNetwork Network) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Network
                .Links.Select(link =>
                    (object?)new object?[] { new JumpBridgeEndpointReply(link.First), new JumpBridgeEndpointReply(link.Second) }
                )
                .ToList(),
            new BlueList<long>(Network.Allowed),
            new BlueList<long>(Network.Denied),
        };
}

internal sealed record JumpBridgeDestinationReply(int SystemId) : BlueModel
{
    public override object ToBlue() => SystemId;
}

internal sealed record JumpBridgeLinkedReply(JumpBridgeEndpoint Endpoint) : BlueModel
{
    public override object ToBlue() => new object?[] { Endpoint.Id, Endpoint.SystemId };
}

internal sealed record JumpBridgeInfoReply(JumpBridgeInfo Info) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "jumpbridge.common.jump_bridge_authorized_info.JumpBridgeAuthorizedInfo",
            new Dictionary<string, object?>
            {
                ["_valid_ship_type"] = Info.ValidShip,
                ["_structure_capacitor"] = Info.Capacitor,
                ["_sim_timestamp"] = Info.AsOf,
            }
        );
}
