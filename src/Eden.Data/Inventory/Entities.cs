using Eden.Data.Docking;
using Eden.Gameplay.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.Inventory;

public sealed class InventoryOwner
{
    public long Id { get; set; }
}

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

public sealed class Ship
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public int TypeId { get; set; }
    public int StationId { get; set; }
    public string Name { get; set; } = "";

    // An assembled hull may belong to a ship bay or wreck. StationId retains
    // its last station until unloading; startup validates the owning container.
    public long? StoredInId { get; set; }
    public int StorageFlagId { get; set; } = (int)InventoryFlag.Hangar;
    public FlightPosition? Position { get; set; }
}
