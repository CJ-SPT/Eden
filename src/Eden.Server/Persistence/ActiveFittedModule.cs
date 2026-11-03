namespace Eden.Server.Persistence;

// Connection-owned activation markers are cleared on shutdown or explicit
// world initialization. Capacitor and paid cooldowns remain durable.
public sealed class ActiveFittedModule
{
    public long ModuleId { get; set; }
    public long ShipId { get; set; }
}
