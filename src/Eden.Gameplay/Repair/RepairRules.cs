using Eden.Gameplay.Market;

namespace Eden.Gameplay.Repair;

public sealed record RepairBudget(long ChargedCents, double Fraction);

public static class RepairRules
{
    public static RepairBudget Allocate(IEnumerable<RepairQuote> quotes, long paymentCents)
    {
        if (paymentCents < 0)
        {
            throw new InvalidDataException("Repair payments cannot be negative.");
        }
        long cost = quotes
            .Where(q => q.Damage > 0)
            .Aggregate(
                0L,
                (total, q) =>
                    checked(
                        total + checked((long)Math.Ceiling(q.Damage)) * RepairDefinitions.IskPerDamagePoint * MarketDefinitions.CentsPerIsk
                    )
            );
        long charged = Math.Min(paymentCents, cost);
        return new(charged, cost == 0 ? 0 : charged / (double)cost);
    }

    public static ShipRepair Apply(long shipId, double armorDamage, double structureDamage, RepairBudget budget) =>
        new(shipId, armorDamage * budget.Fraction, structureDamage * budget.Fraction);
}
