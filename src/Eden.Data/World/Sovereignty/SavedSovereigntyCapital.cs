namespace Eden.Data.World.Sovereignty;

public sealed class SavedSovereigntyCapital
{
    public int AllianceId { get; set; }
    public int? CurrentSystemId { get; set; }
    public int? NewSystemId { get; set; }
    public long? ValidAfter { get; set; }
    public int? PreviousSystemId { get; set; }
    public long CurrentSince { get; set; }
}
