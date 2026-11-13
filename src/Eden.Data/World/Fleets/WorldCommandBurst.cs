namespace Eden.Data.World.Fleets;

public sealed class WorldCommandBurst
{
    public long Id { get; set; }
    public long ShipId { get; set; }
    public int SystemId { get; set; }
    public long ExpiresAt { get; set; }
    public string BoostsJson { get; set; } = "";
}
