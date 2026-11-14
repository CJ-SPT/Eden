using Eden.Gameplay.Characters;

namespace Eden.Data.World.Characters;

public sealed class SavedNotepadEntry
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public string Label { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class SavedSharedSetting
{
    public long Id { get; set; }
    public SharedSettingKind Kind { get; set; }
    public string Hash { get; set; } = "";
    public string Payload { get; set; } = "";
}
