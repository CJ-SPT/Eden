using Eden.Gameplay.Combat;
using Eden.World.Inventory;

namespace Eden.World.Characters;

public sealed class Pilot
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public Account Account { get; set; } = null!;
    public string Name { get; set; } = "";
    public int CorporationId { get; set; }
    public int RaceId { get; set; }
    public int BloodlineId { get; set; }
    public int GenderId { get; set; } = -1;
    public int AncestryId { get; set; }
    public int SchoolId { get; set; }
    public long CreatedAt { get; set; }
    public byte[]? Appearance { get; set; }
    public byte[]? Portrait { get; set; }
    public long ActiveShipId { get; set; }
    public ShipSafetyLevel SafetyLevel { get; set; } = ShipSafetyLevel.Full;
    public Ship ActiveShip { get; set; } = null!;
}
