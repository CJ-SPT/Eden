namespace Eden.World.Skills;

public sealed class TrainingLog
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long Time { get; set; }
    public int TypeId { get; set; }
    public long Points { get; set; }
    public int EventId { get; set; }
}
