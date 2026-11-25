namespace Eden.Data.World.Mining;

public sealed class SavedMiningPollutionCloud
{
    public long Id { get; set; }
    public long AsteroidId { get; set; }
    public int SystemId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public long StartedAt { get; set; }
    public long NextPulseAt { get; set; }
    public long ExpiresAt { get; set; }
}
