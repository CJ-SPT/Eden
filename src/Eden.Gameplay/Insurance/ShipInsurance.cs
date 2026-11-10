using Eden.Gameplay.Market;

namespace Eden.Gameplay.Insurance;

public enum InsuranceLevel
{
    Uninsured = 40,
    Basic = 50,
    Standard = 60,
    Bronze = 70,
    Silver = 80,
    Gold = 90,
    Platinum = 100,
}

public enum InsurancePolicyStatus
{
    Active,
    Cancelled,
    Replaced,
    Expired,
    Paid,
    HullRemoved,
}

public sealed record InsurancePolicy(
    long Id,
    long ShipId,
    long OwnerId,
    int TypeId,
    InsuranceLevel Level,
    long ValueCents,
    long PremiumCents,
    long StartTime,
    long EndTime
);

public sealed record InsurancePurchase(InsurancePolicy Policy, long BalanceCents);

public sealed record InsurancePayout(long ShipId, long AmountCents, long BalanceCents);

public sealed record InsuranceLedgerEntry(long Id, long PilotId, long ShipId, int TypeId, long AmountCents, long BalanceCents, long Time);

public interface IPilotInsurance
{
    void CheckStation(long pilotId, long stationId);
    long Price(int typeId);
    IReadOnlyDictionary<int, long> Prices(IReadOnlyList<int> typeIds);
    IReadOnlyList<InsurancePolicy> Contracts(long pilotId);
    InsurancePolicy? Contract(long pilotId, long shipId);
    InsurancePurchase Insure(long pilotId, long stationId, long shipId, long premiumCents, bool replace);
    void Cancel(long pilotId, long stationId, long shipId);
}

public sealed class InsuranceContractExists(long ownerId) : Exception("This ship already has active insurance.")
{
    public long OwnerId { get; } = ownerId;
}

public static class InsuranceRules
{
    // Build 3662168 insuranceConst: six paid packages; price increment is
    // (coverage fraction - 0.4) * 0.5. Baseline coverage costs nothing.
    public static IReadOnlyList<InsuranceLevel> PaidLevels { get; } =
    [
        InsuranceLevel.Basic,
        InsuranceLevel.Standard,
        InsuranceLevel.Bronze,
        InsuranceLevel.Silver,
        InsuranceLevel.Gold,
        InsuranceLevel.Platinum,
    ];
    public static TimeSpan Duration { get; } = TimeSpan.FromDays(84);
    public const long InsurerCorporationId = 1000113; // Pinned catalog: Pend Insurance.
    public const long FirstJournalId = 9_000_000_000_000;
    public const int StationGroup = 15;

    public static long CatalogValue(double? basePrice)
    {
        // Eden has no retail insurance-price index. Pin valuations to the local
        // catalog's basePrice, independently of the testing market's 1 ISK offers.
        // A 1 ISK minimum preserves distinct, positive premiums at cent precision.
        if (basePrice is null or < 1)
        {
            return 0;
        }
        if (!double.IsFinite(basePrice.Value))
        {
            throw new InvalidDataException("Invalid catalog insurance value.");
        }
        return checked((long)Math.Round((decimal)basePrice.Value * MarketDefinitions.CentsPerIsk, 0, MidpointRounding.ToEven));
    }

    public static long Premium(long valueCents, InsuranceLevel level)
    {
        CheckValue(valueCents);
        if (!PaidLevels.Contains(level))
        {
            throw new InvalidDataException("Choose a paid insurance package.");
        }
        decimal rate = ((int)level - (int)InsuranceLevel.Uninsured) / 200m;
        return checked((long)Math.Round(valueCents * rate, 0, MidpointRounding.ToEven));
    }

    public static InsuranceLevel LevelForPremium(long valueCents, long premiumCents)
    {
        CheckValue(valueCents);
        foreach (var level in PaidLevels)
        {
            if (premiumCents > 0 && Premium(valueCents, level) == premiumCents)
            {
                return level;
            }
        }
        throw new InvalidDataException("The insurance quote has changed or is invalid. Request a new quote.");
    }

    public static long Payout(long valueCents, InsuranceLevel level)
    {
        CheckValue(valueCents);
        if (level != InsuranceLevel.Uninsured && !PaidLevels.Contains(level))
        {
            throw new InvalidDataException("Invalid insurance coverage.");
        }
        return checked((long)Math.Round(valueCents * ((int)level / 100m), 0, MidpointRounding.ToEven));
    }

    public static long PaymentCents(double amount)
    {
        if (!double.IsFinite(amount) || amount <= 0)
        {
            throw new InvalidDataException("Choose a valid insurance premium.");
        }
        return checked((long)Math.Round((decimal)amount * MarketDefinitions.CentsPerIsk, 0, MidpointRounding.ToEven));
    }

    public static long Pay(long balanceCents, long premiumCents)
    {
        if (premiumCents <= 0 || balanceCents < premiumCents)
        {
            throw new InvalidDataException("You do not have enough ISK for this insurance policy.");
        }
        return checked(balanceCents - premiumCents);
    }

    private static void CheckValue(long valueCents)
    {
        if (valueCents < MarketDefinitions.CentsPerIsk)
        {
            throw new InvalidDataException("This hull has no supported insurance value.");
        }
    }
}
