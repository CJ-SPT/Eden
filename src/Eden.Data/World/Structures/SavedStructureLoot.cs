namespace Eden.Data.World.Structures;

public sealed class SavedStructureLoot
{
    public long Id { get; set; }
    public long StructureId { get; set; }
    public long OwnerId { get; set; }
    public int CorporationId { get; set; }
    public int SystemId { get; set; }
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public long ExpiresAt { get; set; }
}
