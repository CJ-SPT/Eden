using Eden.Gameplay.Sovereignty;

namespace Eden.Data.World.Sovereignty;

public sealed class SavedSovereigntyActivityEvent
{
    public long Id { get; set; }
    public int SystemId { get; set; }
    public long At { get; set; }
    public SovereigntyActivityKind Kind { get; set; }
    public double Amount { get; set; }
}
