using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;

namespace Eden.World.Propulsion;

public sealed class WorldTackle
{
    public long ModuleId { get; set; }
    public long PilotId { get; set; }
    public long SourceShipId { get; set; }
    public int TypeId { get; set; }
    public int SystemId { get; set; }
    public long TargetId { get; set; }
    public TackleTargetKind TargetKind { get; set; }
    public DogmaEffect Effect { get; set; }
    public double DurationMilliseconds { get; set; }
    public double CapacitorNeed { get; set; }
    public double Range { get; set; }
    public double Strength { get; set; }
    public double WebPercent { get; set; }
    public double MwdBlock { get; set; }
    public long StartedAt { get; set; }
    public long ExpiresAt { get; set; }

    internal TackleModule Module() =>
        new(ModuleId, TypeId, Effect, DurationMilliseconds, CapacitorNeed, Range, Strength, WebPercent, MwdBlock, 0);

    internal TackleEffect Contact() => new(PilotId, SourceShipId, Module(), new(TargetId, TargetKind), StartedAt, ExpiresAt);
}
