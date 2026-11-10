using Eden.Data.World.Docking;
using Eden.Gameplay.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.World.Inventory;

public sealed class InventoryOwner
{
    public long Id { get; set; }
}

public sealed class OwnedItem
{
    public int MaterialEfficiency { get; set; }
    public int TimeEfficiency { get; set; }
    public int BlueprintRuns { get; set; } = -1;
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
    public long? StructureId { get; set; }
    public long DockedLocationId => StructureId ?? StationId;
    public string Name { get; set; } = "";

    // An assembled hull may belong to a ship bay or wreck. StationId retains
    // its last station until unloading; startup validates the owning container.
    public long? StoredInId { get; set; }
    public int StorageFlagId { get; set; } = (int)InventoryFlag.Hangar;
    public FlightPosition? Position { get; set; }
}
