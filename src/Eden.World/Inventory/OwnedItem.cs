namespace Eden.World.Inventory;

public sealed class OwnedItem
{
    public double? VolumeOverride { get; set; }
    public double CrystalDamage { get; set; }
    public bool Singleton { get; set; }
    public bool Online { get; set; }
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public int TypeId { get; set; }
    public long LocationId { get; set; }
    public int FlagId { get; set; }
    public int Quantity { get; set; }
}
