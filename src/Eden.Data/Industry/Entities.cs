using System.Globalization;
using System.Text.Json;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Economy;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.Industry;

public sealed class IndustryJob
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long BlueprintId { get; set; }
    public int BlueprintTypeId { get; set; }
    public int StationId { get; set; }
    public int ProductTypeId { get; set; }
    public int Runs { get; set; }
    public int OutputQuantity { get; set; }
    public long StartTime { get; set; }
    public long EndTime { get; set; }
    public long DurationSeconds { get; set; }
    public IndustryJobStatus Status { get; set; }
    public long? CompletedAt { get; set; }
    public string MaterialsJson { get; set; } = "{}";
    public long CostCents { get; set; }
}
