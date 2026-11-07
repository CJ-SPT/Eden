namespace Eden.Server.Persistence;

// Shared by paid module cycles and ammunition reloads. Version 25 preserves
// existing WorldWeaponCooldown deadlines and classifies them as ordinary cycles.
public sealed class ModuleCooldown
{
    public long ModuleId { get; set; }
    public long ReadyAt { get; set; }
    public ModuleCooldownKind Kind { get; set; }
}

public enum ModuleCooldownKind
{
    Cycle = 0,
    Reload = 1,
}

public sealed class NpcBounty
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long NpcId { get; set; }
    public int TypeId { get; set; }
    public int SystemId { get; set; }
    public long Time { get; set; }
    public long AmountCents { get; set; }
    public long BalanceCents { get; set; }
    public long TaxCents { get; set; }
}

public sealed class NpcAggression
{
    public long NpcId { get; set; }
    public long TargetShipId { get; set; }
    public long ReadyAt { get; set; }
    public long? LastShotAt { get; set; }
    public long? LastTargetId { get; set; }
    public long MissileReadyAt { get; set; }

    internal static NpcAggression Start(long npcId, long targetShipId, double delayMilliseconds, long now)
    {
        if (!double.IsFinite(delayMilliseconds) || delayMilliseconds < 0)
        {
            throw new InvalidDataException("Invalid NPC reaction delay.");
        }
        return new()
        {
            NpcId = npcId,
            TargetShipId = targetShipId,
            ReadyAt = checked(now + (long)Math.Ceiling(delayMilliseconds * TimeSpan.TicksPerMillisecond)),
            MissileReadyAt = checked(now + (long)Math.Ceiling(delayMilliseconds * TimeSpan.TicksPerMillisecond)),
        };
    }
}

public sealed class ShipLoss
{
    public long ShipId { get; set; }
    public long PilotId { get; set; }
    public long? NpcId { get; set; }
    public long? AttackerPilotId { get; set; }
    public long CapsuleId { get; set; }
    public int TypeId { get; set; }
    public int SystemId { get; set; }
    public long Time { get; set; }
    public string Name { get; set; } = "";
    public string ItemsJson { get; set; } = "[]";
    public string StoredShipsJson { get; set; } = "[]";
    public string DroppedItemIdsJson { get; set; } = "[]";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}
