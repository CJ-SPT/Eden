namespace Eden.Gameplay.Thermal;

public enum NaniteRepairSkill
{
    Operation = 28879,
    Interfacing = 28880,
}

public sealed record NaniteMaterialValue(double BasePrice, long PortionSize, long Quantity);

public sealed record NaniteRepairRates(double HitPointsPerMinute, double CostMultiplier);

public sealed record NaniteRepairQuote(ModuleCondition Condition, long PasteUnits, long DurationTicks);

public static class NaniteRepairRules
{
    public const int PasteType = 28668;

    // Build 3662168 inventoryUtil.GetComputedBasePrice(typeID, 1). The native
    // material path divides by the material portion twice and truncates each
    // material's unit price before summing. Do not replace it with market value.
    public static long ComputedBasePrice(double basePrice, long portionSize, IReadOnlyList<NaniteMaterialValue> materials)
    {
        CheckPrice(basePrice);
        if (portionSize < 0)
        {
            throw new InvalidDataException("Invalid module price portion.");
        }
        double price = materials.Count == 0 ? basePrice / Math.Max(1, portionSize) : 0;
        foreach (var material in materials)
        {
            CheckPrice(material.BasePrice);
            if (material.PortionSize < 0 || material.Quantity < 0)
            {
                throw new InvalidDataException("Invalid module repair material.");
            }
            if (material.PortionSize == 0)
            {
                continue;
            }
            double unit = Math.Truncate(material.BasePrice / material.PortionSize);
            price += material.Quantity * unit / material.PortionSize;
        }
        return checked((long)Math.Truncate(price));
    }

    public static NaniteRepairQuote Quote(ModuleCondition condition, long computedBasePrice, double pasteBasePrice, NaniteRepairRates rates)
    {
        _ = ModuleConditionRules.Define(condition.ItemId, condition.HitPoints, condition.Damage);
        if (
            condition.BurnedOut
            || computedBasePrice < 0
            || !double.IsFinite(pasteBasePrice)
            || pasteBasePrice <= 0
            || !double.IsFinite(rates.HitPointsPerMinute)
            || rates.HitPointsPerMinute <= 0
            || !double.IsFinite(rates.CostMultiplier)
            || rates.CostMultiplier < 0
        )
        {
            throw new InvalidDataException("Nanite paste repairs require a working module and valid repair rates and prices.");
        }
        if (computedBasePrice == 0 || condition.Damage == 0)
        {
            // Native FindNumUnitsToRepairModule clears damage when computed
            // value is zero. Applying that free repair belongs to the command,
            // never to the read-only quote operation.
            return new(condition, 0, 0);
        }
        double unitPrice = computedBasePrice / condition.HitPoints;
        double repairCost = condition.Damage * unitPrice * rates.CostMultiplier;
        long units = checked((long)Math.Ceiling(repairCost / pasteBasePrice));
        long duration = checked((long)Math.Ceiling(condition.Damage / rates.HitPointsPerMinute * TimeSpan.TicksPerMinute));
        return new(condition, units, duration);
    }

    private static void CheckPrice(double value)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new InvalidDataException("Invalid module repair base price.");
        }
    }
}
