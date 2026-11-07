using System.Globalization;
using System.Text.Json;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Economy;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.Insurance;

public sealed class SavedInsuranceLedger
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long ShipId { get; set; }
    public int TypeId { get; set; }
    public long? PolicyId { get; set; }
    public bool IsPayout { get; set; }
    public long AmountCents { get; set; }
    public long BalanceCents { get; set; }
    public long Time { get; set; }

    public InsuranceLedgerEntry Snapshot() => new(Id, PilotId, ShipId, TypeId, AmountCents, BalanceCents, Time);
}

public sealed class SavedInsurancePolicy
{
    public long Id { get; set; }
    public long ShipId { get; set; }
    public long PilotId { get; set; }
    public int TypeId { get; set; }
    public InsuranceLevel Level { get; set; }
    public long ValueCents { get; set; }
    public long PremiumCents { get; set; }
    public long StartTime { get; set; }
    public long EndTime { get; set; }
    public InsurancePolicyStatus Status { get; set; }

    public InsurancePolicy Snapshot() => new(Id, ShipId, PilotId, TypeId, Level, ValueCents, PremiumCents, StartTime, EndTime);
}
