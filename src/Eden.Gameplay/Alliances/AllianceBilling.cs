namespace Eden.Gameplay.Alliances;

public sealed record AllianceBill(
    long Id,
    int AllianceId,
    long IssuedAt,
    long DueAt,
    long AmountCents,
    int CorporationCount,
    long? PaidAt,
    int? PaidByCorporationId,
    int DivisionKey
);

public interface IAllianceBilling
{
    IReadOnlyList<AllianceBill> Bills(long actorId);
    void Pay(long actorId, long billId, int divisionKey);
    long Balance(long actorId);
}

public static class AllianceBillingRules
{
    // Pinned appConst.billTypeAllianceMaintainanceBill; CONCORD owns maintenance invoices.
    public const int MaintenanceBillType = 5;
    public const int ConcordCorporationId = 1000125;
    public const int DowntimeHourUtc = 11;

    // Public references specify monthly billing without a calendar/first-bill anchor.
    // Eden uses fixed 30-day periods from formation, matching its other recurring deadlines.
    public static readonly TimeSpan MaintenancePeriod = TimeSpan.FromDays(30);

    public static long Amount(int corporations) =>
        corporations > 0
            ? checked(corporations * AllianceRules.MaintenancePerCorporationCents)
            : throw new InvalidDataException("A maintenance invoice needs at least one member corporation.");

    public static long NextDowntime(long after)
    {
        var time = DateTime.FromFileTimeUtc(after);
        var next = time.Date.AddHours(DowntimeHourUtc);
        return (time >= next ? next.AddDays(1) : next).ToFileTimeUtc();
    }
}
