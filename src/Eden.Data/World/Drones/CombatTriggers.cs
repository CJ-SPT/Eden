namespace Eden.Data.World.Drones;

internal sealed class WorldDroneCombatTriggers
{
    public long ShipId { get; set; }
    public long OffensiveRevision { get; set; }
    public long OffensiveTargetId { get; set; }
    public long OffensiveAt { get; set; }
    public long IncomingRevision { get; set; }
    public long IncomingSourceId { get; set; }
    public long IncomingAt { get; set; }
}
