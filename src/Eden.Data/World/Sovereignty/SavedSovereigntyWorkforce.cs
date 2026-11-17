using Eden.Gameplay.Sovereignty;

namespace Eden.Data.World.Sovereignty;

public sealed class SavedSovereigntyWorkforce
{
    public long HubId { get; set; }
    public int SystemId { get; set; }
    public SovereigntyWorkforceMode Mode { get; set; }
    public int? Destination { get; set; }
    public long Amount { get; set; }
}
