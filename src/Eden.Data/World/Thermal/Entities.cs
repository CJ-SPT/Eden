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
