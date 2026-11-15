namespace Eden.Data.World.Characters;

public sealed class SavedPilotActivity
{
    public long PilotId { get; set; }
    public long? LoggedOnAt { get; set; }
    public long? LoggedOffAt { get; set; }
}
