using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class PilotNotesHandler(IPilotNotes notes) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "charMgr" || method is not ("GetNote" or "SetNote"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected note arguments.");
            }
            long owner = context.Pilot.PilotId;
            if (method == "GetNote" && args is [long target])
            {
                return new PilotNoteReply(notes.Read(owner, target));
            }
            if (method == "SetNote" && args is [long targetId, var text] && text is string or byte[])
            {
                notes.Save(owner, new(targetId, BlueMarshal.Text(text)));
                return PilotCallResult.None;
            }
            return new PilotCallResult.Failure("Invalid note request.");
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record PilotNoteReply(string Text) : BlueModel
{
    // None leaves PanelNotes.oldText unset, preventing the native close/save path.
    public override object ToBlue() => new BlueUnicode(Text);
}
