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

namespace Eden.Data.World.Industry;

public sealed class IndustryJob
{
    public long? SuspendedAt { get; set; }
    public IndustryActivity Activity { get; set; } = IndustryActivity.Manufacturing;
    public int LicensedRuns { get; set; }
    public bool BlueprintCopy { get; set; }
    public int MaterialEfficiency { get; set; }
    public int TimeEfficiency { get; set; }
    public int OutputRuns { get; set; }
    public double Probability { get; set; } = 1;
    public int? SuccessfulRuns { get; set; }
    public int? OptionalTypeId { get; set; }
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long BlueprintId { get; set; }
    public int BlueprintTypeId { get; set; }
    public long StationId { get; set; }
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
