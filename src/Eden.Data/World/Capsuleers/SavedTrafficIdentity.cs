namespace Eden.Data.World.Capsuleers;

public sealed class SavedTrafficIdentity
{
    public long PilotId { get; set; }
    public long CharacterId { get; set; }
    public long KillId { get; set; }
    public string SnapshotId { get; set; } = "";
}
