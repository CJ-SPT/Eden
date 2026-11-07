using Eden.Gameplay.Economy;
using Eden.Gameplay.Market;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.World.Economy;

public sealed class SavedAdjustedPrice
{
    public int TypeId { get; set; }
    public decimal PriceIsk { get; set; }
}

public sealed class SavedBeltRenewal
{
    public int BeltId { get; set; }
    public int SystemId { get; set; }
    public long NextRenewal { get; set; }
}

public sealed class SavedEconomicPolicy
{
    public int Id { get; set; } = WorldEconomySettings.SingletonId;
    public bool ChargeServiceCosts { get; set; }
    public bool RenewBelts { get; set; } = true;
    public int RenewalHourUtc { get; set; } = 11;
    public decimal DefaultManufacturingIndex { get; set; } = 0.01m;
}

public sealed class SavedManufacturingIndex
{
    public int SystemId { get; set; }
    public decimal Index { get; set; }
}

public sealed class SavedServicePayment
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long CounterpartyId { get; set; }
    public long ContextId { get; set; }
    public WalletEntryType Kind { get; set; }
    public long AmountCents { get; set; }
    public long BalanceCents { get; set; }
    public long Time { get; set; }

    public ServicePayment Snapshot() => new(Id, PilotId, CounterpartyId, ContextId, Kind, AmountCents, BalanceCents, Time);
}
