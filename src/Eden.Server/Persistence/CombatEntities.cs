namespace Eden.Server.Persistence;

public sealed class WeaponCooldown
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
