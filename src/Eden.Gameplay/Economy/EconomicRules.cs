using System.Numerics;
using Eden.Gameplay.Market;

namespace Eden.Gameplay.Economy;

public sealed record AdjustedPrice(int TypeId, decimal PriceIsk);

public sealed record ManufacturingIndex(int SystemId, decimal Index);

public sealed record EconomicConfiguration
{
    public bool ChargeServiceCosts { get; init; } = true;
    public bool RenewBelts { get; init; } = true;
    public int RenewalHourUtc { get; init; } = 11;

    // An explicit offline-world default, not a claim about a live EVE system.
    public decimal DefaultManufacturingIndex { get; init; } = 0.01m;
    public IReadOnlyList<AdjustedPrice> AdjustedPrices { get; init; } = [];
    public IReadOnlyList<ManufacturingIndex> ManufacturingIndices { get; init; } = [];
}

public sealed record ManufacturingCharge(long CostCents, long TaxCents, long SccCents, long SecurityCents = 0)
{
    public long TotalCents => checked(CostCents + TaxCents + SccCents + SecurityCents);
}

public sealed record ServicePayment(
    long Id,
    long PilotId,
    long CounterpartyId,
    long ContextId,
    WalletEntryType Kind,
    long AmountCents,
    long BalanceCents,
    long Time
);

public interface IEconomicPrices
{
    IReadOnlyList<AdjustedPrice> Prices();
}

public static class EconomicRules
{
    // Build 3662168 industry.const, clonegrade and the NPC facility policy.
    public const decimal NpcFacilityTax = 0.0025m;
    public const decimal SccSurcharge = 0.04m;
    public const double ResearchSccDiscount = 0.5;
    public const decimal AlphaTax = 0.0025m;
    public const double SafeSecurityThreshold = 0.95;
    public const decimal IndustrySecurityTax = 0.05m;
    public const long FirstPaymentJournalId = 19_000_000_000_000;

    public static void Validate(EconomicConfiguration configuration)
    {
        if (
            configuration.RenewalHourUtc is < 0 or > 23
            || configuration.DefaultManufacturingIndex is < 0 or > 1
            || configuration.AdjustedPrices is null
            || configuration.ManufacturingIndices is null
            || configuration.AdjustedPrices.Any(p =>
                p is null || p.TypeId <= 0 || p.PriceIsk <= 0 || p.PriceIsk > MarketDefinitions.MaximumOrderPrice
            )
            || configuration.ManufacturingIndices.Any(p => p is null || p.SystemId <= 0 || p.Index is < 0 or > 1)
            || configuration.AdjustedPrices.Select(p => p.TypeId).Distinct().Count() != configuration.AdjustedPrices.Count
            || configuration.ManufacturingIndices.Select(p => p.SystemId).Distinct().Count() != configuration.ManufacturingIndices.Count
        )
        {
            throw new InvalidDataException("Invalid saved economic prices, system indices or belt renewal hour.");
        }
    }

    public static decimal Value(IReadOnlyDictionary<int, int> materials, IReadOnlyDictionary<int, decimal> prices) =>
        materials.Sum(m =>
            m.Value > 0 && prices.TryGetValue(m.Key, out decimal price) && price > 0
                ? checked(m.Value * price)
                : throw new InvalidDataException($"Missing positive adjusted price for material {m.Key}.")
        );

    public static double ClientValue(IReadOnlyDictionary<int, int> materials, IReadOnlyDictionary<int, decimal> prices)
    {
        double value = 0;
        foreach (var material in materials)
        {
            if (!prices.TryGetValue(material.Key, out decimal price) || price <= 0 || material.Value <= 0)
            {
                throw new InvalidDataException($"Missing positive adjusted price for material {material.Key}.");
            }
            value += material.Value * (double)price;
        }
        return value;
    }

    public static ManufacturingCharge Manufacturing(decimal value, decimal index, bool alpha) =>
        ManufacturingFromClientValue((double)value, (double)index, alpha);

    public static ManufacturingCharge ManufacturingFromClientValue(
        double value,
        double index,
        bool alpha,
        double security = 0,
        bool research = false
    )
    {
        if (!double.IsFinite(value) || !double.IsFinite(index) || value < 0 || index is < 0 or > 1)
        {
            throw new InvalidDataException("Invalid manufacturing price inputs.");
        }
        double baseValue = ClientRound(value, 0);
        // The client represents Alpha as an additive CostModifier(1.0025),
        // subtracts one, then adds that product to the system-index cost.
        double alphaCost = alpha ? value * (1 + (double)AlphaTax - 1) : 0;
        return new(
            checked(ClientRound(value * index + alphaCost, 0) * MarketDefinitions.CentsPerIsk),
            checked(ClientRound(baseValue * (double)NpcFacilityTax, 0) * MarketDefinitions.CentsPerIsk),
            checked(
                ClientRound(baseValue * (double)SccSurcharge * (research ? ResearchSccDiscount : 1), 0) * MarketDefinitions.CentsPerIsk
            ),
            security >= SafeSecurityThreshold
                ? checked(ClientRound(baseValue * (double)IndustrySecurityTax, 0) * MarketDefinitions.CentsPerIsk)
                : 0
        );
    }

    public static long ClientRound(double value, int digits)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new InvalidDataException("Invalid service charge.");
        }
        // Python 2.7 rounds exact binary64 values to nearest, ties away from zero.
        // Scaling in double first loses that distinction (2.675 becomes 2.68
        // in .NET, while the pinned interpreter produces 2.67).
        const int fractionBits = 52;
        const int exponentBias = 1023;
        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        int exponent = (int)((bits >> fractionBits) & 0x7ff);
        BigInteger numerator = bits & ((1UL << fractionBits) - 1);
        if (exponent != 0)
        {
            numerator += BigInteger.One << fractionBits;
        }
        int power = (exponent == 0 ? 1 - exponentBias : exponent - exponentBias) - fractionBits;
        BigInteger denominator = BigInteger.One;
        if (power >= 0)
        {
            numerator <<= power;
        }
        else
        {
            denominator <<= -power;
        }
        numerator *= BigInteger.Pow(10, digits);
        BigInteger rounded = BigInteger.DivRem(numerator, denominator, out var remainder);
        return checked((long)(rounded + (remainder * 2 >= denominator ? 1 : 0)));
    }

    public static decimal ReprocessingTax(decimal baseTax, double corporationStanding) =>
        Math.Clamp(baseTax * (1 - (decimal)corporationStanding * 0.15m), 0, 1);

    public static long ReprocessingCharge(int recovered, decimal price, decimal tax) =>
        ClientRound((double)price * (recovered * (double)tax), 2);
}
