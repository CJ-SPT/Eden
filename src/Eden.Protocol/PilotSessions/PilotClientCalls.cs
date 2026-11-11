using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

// Native service callbacks are a wire boundary. No world owner or transaction
// remains held while the player answers a dialog.
internal abstract record PilotClientDialog : BlueModel
{
    internal abstract string Method { get; }

    internal abstract PilotCallResult Complete(object? answer);
}

internal sealed record BoundPilotClientDialog(PilotClientDialog Inner, BlueObjectReference Reference) : PilotClientDialog
{
    internal override string Method => Inner.Method;

    public override object? ToBlue() => Inner.ToBlue();

    internal override PilotCallResult Complete(object? answer) =>
        Inner.Complete(answer) switch
        {
            PilotCallResult.Success success => new PilotCallResult.Success(new BoundPilotReply(Reference, success.Reply)),
            PilotCallResult.ClientCall next => new PilotCallResult.ClientCall(new BoundPilotClientDialog(next.Dialog, Reference)),
            var result => result,
        };
}

internal sealed class PilotClientCalls
{
    private const int MaximumPending = 8;
    private const int DialogTimeoutSeconds = 86_400;
    private long _nextId;
    private readonly Dictionary<long, Pending> _pending = [];

    internal sealed record Pending(IncomingMachoPacket Request, PilotClientDialog Dialog, long PilotId);

    internal BlueInstance Begin(IncomingMachoPacket request, PilotClientDialog dialog, long accountId, long clientId, long pilotId)
    {
        if (_pending.Count >= MaximumPending)
        {
            return request.UserError(new("CustomInfo", new InformationErrorValues("Close the pending agent dialogs first.")));
        }
        long id = checked(++_nextId);
        _pending.Add(id, new(request, dialog, pilotId));
        var source = new BlueInstance(
            "carbon.common.script.net.machoNetAddress.MachoAddress",
            new object?[] { 1, PilotLogin.NodeId, null, id }
        );
        var destination = new BlueInstance(
            "carbon.common.script.net.machoNetAddress.MachoAddress",
            new object?[] { 2, clientId, null, "agents" }
        );
        return new(
            PilotSessionWire.PacketNamespace + "CallReq",
            new object?[]
            {
                6,
                source,
                destination,
                accountId,
                new object?[] { new object?[] { 0, new BlueStream(BlueMarshal.Encode(dialog)) } },
                new Dictionary<string, object?> { ["machoTimeout"] = DialogTimeoutSeconds },
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
            }
        );
    }

    internal bool TryTake(IncomingMachoPacket response, out Pending? pending, out object? answer)
    {
        pending = null;
        answer = null;
        if (
            response.Name is not (PilotSessionWire.PacketNamespace + "CallRsp" or PilotSessionWire.PacketNamespace + "ErrorResponse")
            || response.Fields[2] is not BlueInstance { State: object?[] { Length: 4 } route }
            || route[0] is not 1L
            || !Equals(route[1], (long)PilotLogin.NodeId)
            || route[3] is not long id
            || !_pending.Remove(id, out pending)
        )
        {
            return false;
        }
        if (response.Name == PilotSessionWire.PacketNamespace + "CallRsp")
        {
            try
            {
                answer = ReadAnswer(response.Fields[4]);
            }
            catch (Exception error) when (error is InvalidDataException or EndOfStreamException or OverflowException)
            {
                // A malformed or failed dialog is treated as cancellation.
            }
        }
        return true;
    }

    private static object? ReadAnswer(object? value, int depth = 0)
    {
        if (depth > 4)
        {
            throw new InvalidDataException("Invalid client dialog response nesting.");
        }
        return value switch
        {
            BlueStream stream => BlueMarshal.Decode(stream.Bytes),
            byte[] bytes => BlueMarshal.Decode(bytes),
            object?[] { Length: 1 } tuple => ReadAnswer(tuple[0], depth + 1),
            object?[] { Length: 2 } pair when pair[0] is long => ReadAnswer(pair[1], depth + 1),
            _ => throw new InvalidDataException("Missing client dialog response stream."),
        };
    }
}
