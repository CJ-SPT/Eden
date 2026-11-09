using Eden.Infrastructure.Registration;
using Eden.Modding;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Modding;

[Injectable]
internal sealed class ModCommandHandler(IModCommands commands) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "edenMods" || method != "Execute")
        {
            return PilotCallResult.Unsupported;
        }
        if (args is not [string or byte[], string or byte[], string or byte[]] || keywords is { Count: > 0 })
        {
            return new ModCommandWireReply(ModCommandReply.Failed("Supply a mod ID, command name and argument text."));
        }
        string id = BlueMarshal.Text(args[0]);
        string command = BlueMarshal.Text(args[1]);
        string arguments = BlueMarshal.Text(args[2]);
        if (id.Length > 100 || command.Length > 100 || arguments.Length > ModFormat.MaximumCommandBytes)
        {
            return new ModCommandWireReply(ModCommandReply.Failed("The mod command is too large."));
        }
        return new ModCommandWireReply(commands.Execute(id, command, new(ModSessionObserver.Pilot(context), arguments)));
    }
}

internal sealed record ModCommandWireReply(ModCommandReply Reply) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["success"] = Reply.Success,
            ["json"] = Reply.Json,
            ["error"] = Reply.Error,
        };
}
