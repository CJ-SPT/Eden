using Eden.Gameplay.Combat;

namespace Eden.Data.World.Combat;

public sealed class SavedKillRight
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public long TargetId { get; set; }
    public long ExpiresAt { get; set; }
    public long? PriceCents { get; set; }
    public long? RestrictedTo { get; set; }
    public long ActiveUntil { get; set; }

    public KillRight Snapshot() => new(Id, OwnerId, TargetId, ExpiresAt, PriceCents, RestrictedTo);
}
