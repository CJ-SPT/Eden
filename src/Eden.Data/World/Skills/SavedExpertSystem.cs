namespace Eden.Data.World.Skills;

public sealed class SavedExpertSystem
{
    public long PilotId { get; set; }
    public int TypeId { get; set; }
    public long InstalledAt { get; set; }
    public long ExpiresAt { get; set; }
}
