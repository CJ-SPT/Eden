namespace Eden.Data.World.Thermal;

public sealed class SavedModuleCondition
{
    public long ModuleId { get; set; }
    public double Damage { get; set; }
}

public sealed class SavedShipHeat
{
    public long ShipId { get; set; }
    public string StateJson { get; set; } = "";
}

public sealed class SavedNaniteRepair
{
    public long ModuleId { get; set; }
    public Guid Generation { get; set; }
    public long PilotId { get; set; }
    public long ShipId { get; set; }
    public long StartedAt { get; set; }
    public long CompletesAt { get; set; }
    public double InitialDamage { get; set; }
    public double InitialHitPoints { get; set; }
    public long PasteUnits { get; set; }
}
