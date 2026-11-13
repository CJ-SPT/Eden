namespace Eden.Data.World.Loot;

public sealed class SavedJetcan
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public long ShipId { get; set; }
    public int CorporationId { get; set; }
    public int SystemId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public long ExpiresAt { get; set; }
}

public sealed class SavedJettisonCooldown
{
    public long PilotId { get; set; }
    public long ReadyAt { get; set; }
}
