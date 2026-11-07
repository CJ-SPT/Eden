using System.Text.Json;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.Fittings;

// Connection-owned activation markers are cleared on shutdown or explicit
// world initialization. Capacitor and paid cooldowns remain durable.
public sealed class ActiveFittedModule
{
    public long ModuleId { get; set; }
    public long ShipId { get; set; }
}

public sealed class SavedFittingRecord
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public int ShipTypeId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ItemsJson { get; set; } = "[]";
    public long SavedAt { get; set; }

    public SavedFitting Snapshot() =>
        new(Id, OwnerId, new(Name, Description, ShipTypeId, JsonSerializer.Deserialize<SavedFittingItem[]>(ItemsJson)!), SavedAt);
}

public sealed class ShipEnergy
{
    public long ShipId { get; set; }
    public double Charge { get; set; }
    public long UpdatedAt { get; set; }
}
