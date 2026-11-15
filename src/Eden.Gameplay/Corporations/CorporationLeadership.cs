using System.Numerics;

namespace Eden.Gameplay.Corporations;

public sealed record CorporationDividendRecipient(long OwnerId, long Weight);

public sealed record CorporationDividendPayment(long OwnerId, long Cents);

public interface ICorporationLeadership
{
    void PayDividend(long actorId, bool shareholders, long amountCents);
    IReadOnlyList<long> Successors(long actorId);
    void Transfer(long actorId, long successorId);
    void Close(long actorId);
}

public static class CorporationDividendRules
{
    public static IReadOnlyList<CorporationDividendPayment> Allocate(
        long amountCents,
        IReadOnlyList<CorporationDividendRecipient> recipients
    )
    {
        if (
            amountCents <= 0
            || recipients.Count == 0
            || recipients.Any(row => row.OwnerId <= 0 || row.Weight <= 0)
            || recipients.Select(row => row.OwnerId).Distinct().Count() != recipients.Count
        )
        {
            throw new InvalidDataException("Choose a positive dividend with valid recipients.");
        }
        BigInteger total = recipients.Aggregate(BigInteger.Zero, (sum, row) => sum + row.Weight);
        var portions = recipients
            .Select(row =>
            {
                BigInteger numerator = (BigInteger)amountCents * row.Weight;
                return new Portion(row.OwnerId, (long)(numerator / total), numerator % total);
            })
            .OrderByDescending(row => row.Remainder)
            .ThenBy(row => row.OwnerId)
            .ToArray();
        long remainder = amountCents - portions.Sum(row => row.Cents);
        return portions
            .Select((row, index) => new CorporationDividendPayment(row.OwnerId, row.Cents + (index < remainder ? 1 : 0)))
            .Where(row => row.Cents > 0)
            .OrderBy(row => row.OwnerId)
            .ToArray();
    }

    private sealed record Portion(long OwnerId, long Cents, BigInteger Remainder);
}
