using Eden.Gameplay.Inventory;
using Eden.World.Docking;

namespace Eden.World.Inventory;

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
