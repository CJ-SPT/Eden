using Eden.Gameplay.Combat;

namespace Eden.Data.World.Combat.Npcs;

public sealed class WorldNpcEnergy
{
    public long NpcId { get; set; }
    public double Charge { get; set; }
    public long UpdatedAt { get; set; }

    public NpcCapacitorState State() => new(Charge, UpdatedAt);
}
