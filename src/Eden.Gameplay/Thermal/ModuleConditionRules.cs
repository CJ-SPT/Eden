namespace Eden.Gameplay.Thermal;

public sealed record ModuleCondition(long ItemId, double HitPoints, double Damage)
{
    public bool BurnedOut => Damage >= HitPoints;
}

public sealed record ModuleConditionChange(ModuleCondition Previous, ModuleCondition Current, bool WentOffline);

public static class ModuleConditionRules
{
    public static ModuleCondition Define(long item, double hitPoints, double damage)
    {
        if (!double.IsFinite(hitPoints) || hitPoints <= 0 || !double.IsFinite(damage) || damage < 0)
        {
            throw new InvalidDataException("Invalid module condition.");
        }
        return new(item, hitPoints, damage);
    }

    public static ModuleCondition ApplyDamage(ModuleCondition condition, double incoming)
    {
        _ = Define(condition.ItemId, condition.HitPoints, condition.Damage);
        CheckAmount(incoming);
        // A capacity change must not erase existing damage on an already
        // burned-out module. New heat damage saturates at the current HP.
        return condition.BurnedOut
            ? condition
            : condition with
            {
                Damage = condition.Damage + Math.Min(incoming, condition.HitPoints - condition.Damage),
            };
    }

    public static ModuleCondition Repair(ModuleCondition condition, double amount)
    {
        _ = Define(condition.ItemId, condition.HitPoints, condition.Damage);
        CheckAmount(amount);
        return condition with { Damage = Math.Max(0, condition.Damage - amount) };
    }

    private static void CheckAmount(double amount)
    {
        if (!double.IsFinite(amount) || amount < 0)
        {
            throw new InvalidDataException("Invalid module damage or repair amount.");
        }
    }
}
