namespace Eden.Gameplay.Market;

public sealed record MarketFill(int Quantity, long GrossCents, long TaxCents);

public static class MarketRules
{
    public static decimal SalesTax(int accountingLevel) =>
        MarketDefinitions.SalesTaxRate * (1 - MarketDefinitions.AccountingReductionPerLevel * accountingLevel);

    public static MarketFill Fill(int requested, int remaining, long priceCents, decimal taxRate = 0)
    {
        if (requested <= 0 || remaining <= 0 || priceCents <= 0 || taxRate < 0 || taxRate > 1)
        {
            throw new InvalidDataException("Invalid market fill.");
        }
        int quantity = Math.Min(requested, remaining);
        long gross = checked(priceCents * quantity);
        return new(quantity, gross, checked((long)Math.Round(gross * taxRate, 0, MidpointRounding.ToEven)));
    }
}
