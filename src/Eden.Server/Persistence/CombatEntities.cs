namespace Eden.Server.Persistence;

// Shared by weapon and shield-booster cycles. The legacy WorldWeaponCooldown
// table name is retained so existing saves need no migration or clock reset.
public sealed class ModuleCooldown
{
    public long ModuleId { get; set; }
    public long ReadyAt { get; set; }
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
}

public sealed class NpcAggression
{
    public long NpcId { get; set; }
    public long TargetShipId { get; set; }
    public long ReadyAt { get; set; }
    public long? LastShotAt { get; set; }
}

public sealed class ShipLoss
{
    public long ShipId { get; set; }
    public long PilotId { get; set; }
    public long NpcId { get; set; }
    public long CapsuleId { get; set; }
    public int TypeId { get; set; }
    public int SystemId { get; set; }
    public long Time { get; set; }
    public string Name { get; set; } = "";
    public string ItemsJson { get; set; } = "[]";
    public string DroppedItemIdsJson { get; set; } = "[]";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}
