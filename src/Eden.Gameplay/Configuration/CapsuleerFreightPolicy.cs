using System.Globalization;

namespace Eden.Gameplay.Configuration;

public sealed record CapsuleerFreightPolicy(
    bool VariedHulls = true,
    long OrdinaryMinimumIsk = 150_000,
    long OrdinaryMaximumIsk = 750_000,
    long CourierMinimumIsk = 800_000,
    long CourierMaximumIsk = 4_000_000,
    int CourierPercent = 8,
    int EmptyReturnPercent = 14,
    int WreckSurvivalPercent = 50,
    string CargoTypeIds = ""
)
{
    public const long MaximumBudget = 1_000_000_000_000;
    public const int MaximumTypes = 64;
    public static CapsuleerFreightPolicy Default { get; } = new();

    public void Validate()
    {
        if (
            OrdinaryMinimumIsk < 0
            || OrdinaryMinimumIsk > OrdinaryMaximumIsk
            || OrdinaryMaximumIsk > MaximumBudget
            || CourierMinimumIsk < 0
            || CourierMinimumIsk > CourierMaximumIsk
            || CourierMaximumIsk > MaximumBudget
            || CourierPercent is < 0 or > 100
            || EmptyReturnPercent is < 0 or > 100
            || WreckSurvivalPercent is < 0 or > 100
        )
        {
            throw new InvalidDataException(
                "Freight budgets must be ordered and within the supported range, and percentages must be between 0 and 100."
            );
        }
        _ = TypeIds();
    }

    public int[] TypeIds()
    {
        if (string.IsNullOrWhiteSpace(CargoTypeIds))
        {
            return [];
        }
        var entries = CargoTypeIds.Split(',', StringSplitOptions.TrimEntries);
        if (entries.Length > MaximumTypes)
        {
            throw new InvalidDataException("Choose up to 64 freight item types.");
        }
        HashSet<int> ids = [];
        foreach (var entry in entries)
        {
            if (!int.TryParse(entry, NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id <= 0 || !ids.Add(id))
            {
                throw new InvalidDataException("Enter distinct positive cargo type IDs separated by commas.");
            }
        }
        return ids.Order().ToArray();
    }
}
