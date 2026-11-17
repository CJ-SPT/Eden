namespace Eden.Data.World.Sovereignty;

public sealed class SavedSovereigntyActivity
{
    public int SystemId { get; set; }
    public double MilitaryPoints { get; set; }
    public double IndustrialPoints { get; set; }
    public long UpdatedAt { get; set; }
    public long LastMilitaryAt { get; set; }
    public long LastIndustrialAt { get; set; }
    public long Revision { get; set; }
}
