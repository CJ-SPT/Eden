namespace Eden.Data.World.Sovereignty;

// A completed claim, separate from a neutral deployed hub. Deployment and
// Entosis will create this record together only when a capture has completed.
public sealed class SavedSovereigntyClaim
{
    public int SystemId { get; set; }
    public long HubId { get; set; }
    public int CorporationId { get; set; }
    public int AllianceId { get; set; }
    public long ClaimedAt { get; set; }
}
