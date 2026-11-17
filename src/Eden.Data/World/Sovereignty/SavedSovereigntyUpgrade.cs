using Eden.Gameplay.Sovereignty;

namespace Eden.Data.World.Sovereignty;

public sealed class SavedSovereigntyUpgrade
{
    public long Id { get; set; }
    public long HubId { get; set; }
    public int SystemId { get; set; }
    public int TypeId { get; set; }
    public int Priority { get; set; }
    public bool ConfiguredOnline { get; set; }
    public SovereigntyUpgradePower PowerState { get; set; }
    public bool StartupPaid { get; set; }
    public long? RunningSince { get; set; }
}
