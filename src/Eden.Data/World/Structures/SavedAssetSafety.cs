namespace Eden.Data.World.Structures;

public sealed class SavedAssetSafety
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public long StructureId { get; set; }
    public int SystemId { get; set; }
    public string Name { get; set; } = "";
    public long CreatedAt { get; set; }
    public int NearestStationId { get; set; }
    public long? DestinationId { get; set; }
    public long? DeliveredAt { get; set; }
}
