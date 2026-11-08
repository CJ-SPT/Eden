namespace Eden.Gameplay.Thermal;

public enum NaniteRepairSkill
{
    Operation = 28879,
    Interfacing = 28880,
}

public sealed record NaniteMaterialValue(double BasePrice, long PortionSize, long Quantity);

public sealed record NaniteRepairRates(double HitPointsPerMinute, double CostMultiplier);

public sealed record NaniteRepairQuote(ModuleCondition Condition, long PasteUnits, long DurationTicks)
{
    public double PasteUnitsPerDamage { get; init; }
}

public static class NaniteRepairRules
{
    public const int PasteType = 28668;

    public static NaniteRepairSchedule? Start(NaniteRepairQuote quote, long pilot, long ship, long now, Guid generation)
    {
        _ = ModuleConditionRules.Define(quote.Condition.ItemId, quote.Condition.HitPoints, quote.Condition.Damage);
        if (
            pilot <= 0
            || ship <= 0
            || quote.Condition.ItemId <= 0
            || now < 0
            || generation == Guid.Empty
            || quote.Condition.BurnedOut
            || quote.Condition.Damage <= 0
            || !double.IsFinite(quote.PasteUnitsPerDamage)
            || quote.PasteUnitsPerDamage < 0
            || quote.PasteUnits < 0
            || quote.DurationTicks < 0
            || quote.DurationTicks == 0 && quote.PasteUnits != 0
        )
        {
            throw new InvalidDataException("Invalid paid nanite repair.");
        }
        return quote.DurationTicks == 0
            ? null
            : new(
                new(quote.Condition.ItemId, now, checked(now + quote.DurationTicks), generation),
                pilot,
                ship,
                quote.Condition.Damage,
                quote.Condition.HitPoints,
                quote.PasteUnits
            )
            {
                PasteUnitsPerDamage = quote.PasteUnitsPerDamage,
            };
    }

    public static double Repaired(NaniteRepairSchedule schedule, long now)
    {
        Validate(schedule);
        if (now < 0)
        {
            throw new InvalidDataException("Invalid nanite repair clock.");
        }
        if (now <= schedule.Key.StartedAt)
        {
            return 0;
        }
        if (now >= schedule.Key.CompletesAt)
        {
            return schedule.InitialDamage;
        }
        return schedule.InitialDamage * ((now - schedule.Key.StartedAt) / (double)(schedule.Key.CompletesAt - schedule.Key.StartedAt));
    }

    public static long Refund(NaniteRepairSchedule schedule, long now)
    {
        Validate(schedule);
        // Older reservations preserve their original prepaid contract.
        if (schedule.PasteUnitsPerDamage == 0 || now >= schedule.Key.CompletesAt)
        {
            return 0;
        }
        double used = Math.Min(schedule.PasteUnits, Math.Ceiling(Repaired(schedule, now) * schedule.PasteUnitsPerDamage));
        return schedule.PasteUnits - checked((long)used);
    }

    public static void Validate(NaniteRepairSchedule schedule)
    {
        ValidateKey(schedule.Key);
        if (
            schedule.PilotId <= 0
            || schedule.ShipId <= 0
            || !double.IsFinite(schedule.InitialDamage)
            || schedule.InitialDamage <= 0
            || !double.IsFinite(schedule.InitialHitPoints)
            || schedule.InitialHitPoints <= schedule.InitialDamage
            || !double.IsFinite(schedule.PasteUnitsPerDamage)
            || schedule.PasteUnitsPerDamage < 0
            || schedule.PasteUnits < 0
        )
        {
            throw new InvalidDataException("Invalid saved nanite repair.");
        }
    }

    public static void ValidateKey(NaniteRepairKey key)
    {
        if (key.ModuleId <= 0 || key.StartedAt < 0 || key.CompletesAt <= key.StartedAt || key.Generation == Guid.Empty)
        {
            throw new InvalidDataException("Invalid nanite repair identity.");
        }
    }

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
        long duration = Math.Max(1, checked((long)Math.Ceiling(condition.Damage / rates.HitPointsPerMinute * TimeSpan.TicksPerMinute)));
        return new(condition, units, duration) { PasteUnitsPerDamage = unitPrice * rates.CostMultiplier / pasteBasePrice };
    }

    private static void CheckPrice(double value)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new InvalidDataException("Invalid module repair base price.");
        }
    }
}
