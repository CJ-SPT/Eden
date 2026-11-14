namespace Eden.Gameplay.Characters;

public sealed record NotepadEntry(long Id, string Label, string Text);

public interface IPilotNotepad
{
    IReadOnlyList<NotepadEntry> List(long pilotId);
    NotepadEntry? Read(long pilotId, long noteId);
    long Add(long pilotId, string label, string text);
    void Edit(long pilotId, long noteId, string label, string? text);
    void Remove(long pilotId, long noteId);
}

public static class NotepadRules
{
    // Build 3662168 notepad.py: 80-character titles plus "N:", and 6000-character folder chunks.
    public const int MaximumLabelLength = 82;
    public const int MaximumTextLength = 6000;

    public static void Check(string label, string? text)
    {
        if (string.IsNullOrEmpty(label) || label.Length > MaximumLabelLength || label.Contains('\0'))
        {
            throw new InvalidDataException("Invalid notepad label.");
        }
        if (text is not null && (text.Length > MaximumTextLength || text.Contains('\0')))
        {
            throw new InvalidDataException("Invalid notepad text.");
        }
    }
}
