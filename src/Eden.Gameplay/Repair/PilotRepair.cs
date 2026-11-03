using Eden.Gameplay.Market;

namespace Eden.Gameplay.Repair;

public sealed record RepairQuote(long ItemId, int TypeId, int GroupId, double Damage, double MaxHealth);

public sealed record ShipRepair(long ShipId, double ArmorRepaired, double StructureRepaired);

public sealed record RepairResult(long BalanceCents, IReadOnlyList<ShipRepair> Ships);

public sealed record RepairPayment(
    long Id,
    long PilotId,
    int StationId,
    long StationOwnerId,
    long AmountCents,
    long BalanceCents,
    long Time
);

public interface IPilotRepair
{
    void CheckStation(long pilotId, int stationId);
    IReadOnlyDictionary<long, IReadOnlyList<RepairQuote>> Quotes(long pilotId, int stationId, IReadOnlyList<long> itemIds);
    RepairResult Repair(long pilotId, int stationId, IReadOnlyList<long> itemIds, long paymentCents, long now);
    IReadOnlyList<RepairPayment> Payments(long pilotId);
}

public static class RepairDefinitions
{
    // Eden pricing policy. The client rounds each quote's damage upward before
    // multiplying this rate, and allows an ISK budget for partial ship repairs.
    public const int IskPerDamagePoint = 1;
    public const int StationGroup = 15;
    public const long FirstRepairJournalId = 7_000_000_000_000;

    public static long PaymentCents(double payment)
    {
        if (!double.IsFinite(payment) || payment < 0 || payment > (double)MarketDefinitions.MaximumOrderPrice)
        {
            throw new InvalidDataException("Enter a nonnegative repair payment within the client limit.");
        }
        return checked((long)Math.Round((decimal)payment * MarketDefinitions.CentsPerIsk, 0, MidpointRounding.ToEven));
    }
}
