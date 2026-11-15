namespace Eden.Data.World.Exploration;

public sealed class SavedAbyssalParticipant
{
    public long PilotId { get; set; }
    public long RunId { get; set; }
    public long ShipId { get; set; }
    public bool Finished { get; set; }
    public bool Entered { get; set; } = true;
    public int Room { get; set; }
    public long OutsideSince { get; set; }
}
