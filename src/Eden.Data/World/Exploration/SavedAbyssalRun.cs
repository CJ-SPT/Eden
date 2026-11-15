namespace Eden.Data.World.Exploration;

public sealed class SavedAbyssalRun
{
    public long Id { get; set; }
    public long OwnerPilotId { get; set; }
    public int SystemId { get; set; }
    public int OriginSystemId { get; set; }
    public double OriginX { get; set; }
    public double OriginY { get; set; }
    public double OriginZ { get; set; }
    public int FilamentTypeId { get; set; }
    public int Mode { get; set; }
    public int Tier { get; set; }
    public int Weather { get; set; }
    public int Room { get; set; }
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public int Status { get; set; }
    public int Seed { get; set; }
    public long EnvironmentAt { get; set; }
    public int DisabledFields { get; set; }
}
