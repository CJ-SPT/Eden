namespace Eden.Data.World.Exploration;

public sealed class WorldCombatAnomaly
{
    public long Id { get; set; }
    public int SystemId { get; set; }
    public int Wave { get; set; } = -1;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public long? CompletedAt { get; set; }
    public long? ReplaceAt { get; set; }
}
