namespace Eden.Gameplay.Characters;

public sealed record PilotNote(long TargetId, string Text);

public interface IPilotNotes
{
    string Read(long pilotId, long targetId);
    void Save(long pilotId, PilotNote note);
}

public static class PilotNoteRules
{
    // Build 3662168 info/panels/panelNotes.py: editor limit and SaveNote text slice.
    public const int MaximumLength = 5000;

    public static void CheckTarget(long targetId)
    {
        if (targetId <= 0)
        {
            throw new InvalidDataException("Choose a valid note target.");
        }
    }

    public static void Check(PilotNote note)
    {
        CheckTarget(note.TargetId);
        if (note.Text.Length > MaximumLength || note.Text.Contains('\0'))
        {
            throw new InvalidDataException($"Notes must contain at most {MaximumLength} characters and no nulls.");
        }
    }
}
