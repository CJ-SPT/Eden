using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class NotepadHandler(IPilotNotepad notes) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            service != "charMgr"
            || method is not ("GetOwnerNoteLabels" or "GetOwnerNote" or "AddOwnerNote" or "EditOwnerNote" or "RemoveOwnerNote")
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected notepad arguments.");
            }
            long owner = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("GetOwnerNoteLabels", []):
                    return new CompatibilityRowset(["noteID", "label"], notes.List(owner).Select(n => new NotepadLabelRow(n)).ToArray());
                case ("GetOwnerNote", [long id]):
                    return new NotepadReply(notes.Read(owner, id) ?? new(id, "", ""));
                case ("AddOwnerNote", [var label, var text]):
                    return notes.Add(owner, ReadText(label), ReadText(text));
                case ("EditOwnerNote", [long id, var label]):
                    notes.Edit(owner, id, ReadText(label), null);
                    return PilotCallResult.None;
                case ("EditOwnerNote", [long id, var label, var text]):
                    notes.Edit(owner, id, ReadText(label), text is null ? null : ReadText(text));
                    return PilotCallResult.None;
                case ("RemoveOwnerNote", [long id]):
                    notes.Remove(owner, id);
                    return PilotCallResult.None;
                default:
                    return new PilotCallResult.Failure("Invalid notepad request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static string ReadText(object? value) =>
        value is string or byte[] ? BlueMarshal.Text(value) : throw new InvalidDataException("Expected notepad text.");
}

internal sealed record NotepadLabelRow(NotepadEntry Entry) : BlueModel
{
    public override object ToBlue() => new object?[] { Entry.Id, new BlueUnicode(Entry.Label) };
}

internal sealed record NotepadReply(NotepadEntry Entry) : BlueModel
{
    public override object ToBlue() => new BlueList<NotepadRow>([new(Entry)]);
}

internal sealed record NotepadRow(NotepadEntry Entry) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["noteID"] = Entry.Id,
                ["label"] = new BlueUnicode(Entry.Label),
                ["note"] = new BlueUnicode(Entry.Text),
            }
        );
}
