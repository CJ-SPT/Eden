namespace Eden.Data.World.Corporations;

public sealed class SavedCorporationRemoval
{
    public long PilotId { get; set; }
    public int CorporationId { get; set; }
    public long ActorId { get; set; }
    public long MembershipId { get; set; }
    public int DestinationCorporationId { get; set; }
    public long RequestedAt { get; set; }
}
