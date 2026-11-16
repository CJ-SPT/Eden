namespace Eden.Data.World.Sovereignty;

public sealed class SavedSovereigntyPrime
{
    public int AllianceId { get; set; }
    public int CurrentHour { get; set; }
    public int? NewHour { get; set; }
    public long? ValidAfter { get; set; }
}
