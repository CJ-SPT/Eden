using System.Text.Json;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Repair;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.World.Repair;

public sealed class PendingLocalRepair
{
    public RepairLayer Layer { get; set; }
    public long ModuleId { get; set; }
    public long ShipId { get; set; }
    public long StartedAt { get; set; }
    public long CompletesAt { get; set; }
    public double Amount { get; set; }
}

public sealed class StationRepairPayment
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long StationId { get; set; }
    public long StationOwnerId { get; set; }
    public long AmountCents { get; set; }
    public long BalanceCents { get; set; }
    public long Time { get; set; }
    public required string RepairsJson { get; set; }
}

public sealed class WorldRemoteRepair
{
    public long ModuleId { get; set; }
    public long PilotId { get; set; }
    public long SourceShipId { get; set; }
    public long TargetId { get; set; }
    public int SystemId { get; set; }
    public string CycleJson { get; set; } = string.Empty;
}
