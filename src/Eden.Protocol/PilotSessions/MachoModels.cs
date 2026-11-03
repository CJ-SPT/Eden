using Eden.Gameplay.Docking;
using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

internal abstract record MachoAddress : BlueModel
{
    public abstract override object ToBlue();

    internal sealed record Node(long Id) : MachoAddress
    {
        public override object ToBlue() => Address(1, Id, null, null);
    }

    internal sealed record Client(long Id) : MachoAddress
    {
        public override object ToBlue() => Address(2, Id, null, null);
    }

    internal sealed record Broadcast(string Event, long ClientId) : MachoAddress
    {
        public override object ToBlue() => Address(4, Event, new List<object?> { ClientId }, "clientID");
    }

    private static BlueInstance Address(params object?[] fields) => new("carbon.common.script.net.machoNetAddress.MachoAddress", fields);
}

// Arguments remain inert Python values until the owning service adapter parses them.
internal sealed record PilotCallRequest(object? ObjectId, string Method, object? Arguments, Dictionary<string, object?>? Keywords)
{
    public static PilotCallRequest Read(byte[] bytes)
    {
        var fields = BlueRead.Tuple(BlueMarshal.Decode(bytes), 4);
        return new(fields[0], BlueMarshal.Text(fields[1]), fields[2], fields[3] as Dictionary<string, object?>);
    }

    public bool IsBindingWithoutCall => Arguments is object?[] { Length: 2 } bind && bind[1] is null;
}

internal sealed record PilotSessionChange(long AccountId, long ClientId, DockedPilot Pilot, DockedPilot? Previous = null) : BlueModel
{
    public override object ToBlue() => PilotSessionWire.SessionChange(AccountId, ClientId, Pilot, Previous);
}

internal sealed record AccountSessionState(long AccountId) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["userid"] = AccountId,
            ["role"] = PilotLogin.PlayerRole,
            ["languageID"] = "EN",
            ["userType"] = 1,
        };
}

// Opaque MachoNet forwarding fields stay in this wire adapter, not in the session loop.
internal sealed record IncomingMachoPacket(string Name, object?[] Fields)
{
    public static IncomingMachoPacket Read(object? value)
    {
        if (value is not BlueInstance packet)
        {
            throw new InvalidDataException("Expected a MachoNet packet.");
        }
        return new(packet.Name, BlueRead.Tuple(packet.State, 14));
    }

    public byte[] CallBytes()
    {
        var body = BlueRead.Tuple(Fields[4], 1);
        var wrapper = BlueRead.Tuple(body[0], 2);
        return wrapper[1] is BlueStream stream ? stream.Bytes : BlueRead.Bytes(wrapper[1]);
    }

    public object?[] Destination()
    {
        if (Fields[2] is not BlueInstance destination || destination.State is not object?[] route)
        {
            throw new InvalidDataException("Missing call destination.");
        }
        return route;
    }

    private BlueInstance Response(string name, int command, object? body) => PilotSessionWire.Response(Fields, name, command, body);

    public BlueInstance CallSucceeded(BlueModel reply) => Response("CallRsp", 7, new object?[] { BlueMarshal.Encode(reply) });

    public BlueInstance InvalidArguments() => Response("ErrorResponse", 15, new object?[] { 6, 0, "Unsupported local call arguments" });

    public BlueInstance UnsupportedCall(string service, string method) =>
        Response("ErrorResponse", 15, new object?[] { 6, 0, $"Unsupported local call {service}.{method}" });

    public BlueInstance UserError(PilotCallResult.NamedFailure error)
    {
        byte[] exception = BlueMarshal.Encode(new BlueConstructor("exceptions.UserError", [error.Name, error.Values]));
        return Response("ErrorResponse", 15, new object?[] { 6, 2, new object?[] { exception } });
    }

    public BlueInstance Ping(long now)
    {
        var times = BlueRead.Tuple(Fields[4], 1)[0] as List<object?> ?? throw new InvalidDataException("Missing ping times.");
        foreach (
            string phase in new[]
            {
                "proxy::handle_message",
                "proxy::writing",
                "server::turnaround",
                "proxy::handle_message",
                "proxy::writing",
            }
        )
        {
            times.Add(new object?[] { now, now, phase });
        }
        return Response("PingRsp", 21, new object?[] { times });
    }
}
