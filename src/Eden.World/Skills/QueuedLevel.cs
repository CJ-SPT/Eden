using Microsoft.EntityFrameworkCore;

namespace Eden.World.Skills;

[PrimaryKey(nameof(PilotId), nameof(Position))]
public sealed class QueuedLevel
{
    public long PilotId { get; set; }
    public int Position { get; set; }
    public int TypeId { get; set; }
    public int Level { get; set; }
    public long StartPoints { get; set; }
    public long EndPoints { get; set; }
    public long? StartTime { get; set; }
    public long? EndTime { get; set; }
    public string PeriodsJson { get; set; } = "[]";
}
