namespace Eden.Data.World.Capsuleers;

public sealed class SavedCapsuleerHauler
{
    public int Id { get; set; }
    public long PilotId { get; set; }
    public int SourceStationId { get; set; }
    public int DestinationStationId { get; set; }
    public long FounderShipId { get; set; }
    public long StartingCents { get; set; }
    public string? Progress { get; set; }
}
