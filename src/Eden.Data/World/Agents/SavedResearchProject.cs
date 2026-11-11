namespace Eden.Data.World.Agents;

public sealed class SavedResearchProject
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long AgentId { get; set; }
    public int SkillId { get; set; }
    public double Points { get; set; }
    public double PointsPerDay { get; set; }
    public long SettledAt { get; set; }
}
