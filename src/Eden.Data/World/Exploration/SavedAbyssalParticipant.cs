namespace Eden.Data.World.Exploration;

public sealed class SavedAbyssalParticipant
{
    public long PilotId { get; set; }
    public long RunId { get; set; }
    public long ShipId { get; set; }
    public bool Finished { get; set; }
    public long OutsideSince { get; set; }
}
