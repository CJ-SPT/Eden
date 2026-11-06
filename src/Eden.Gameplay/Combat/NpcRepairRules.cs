using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Combat;

public sealed record NpcRepairCycle(double Amount, long DurationTicks, double DelayChance);

public sealed record NpcRepairProfile(NpcRepairCycle? Shield, NpcRepairCycle? Armor);

public sealed record NpcRepairState(long ShieldReadyAt = 0, long ArmorReadyAt = 0, long Revision = 0);

public sealed record NpcRepairResult(LiveHealth Health, NpcRepairState State);

public static class NpcRepairRules
{
    // Build 3662168 dogma defaults: 636=10000 ms, 638=0, 639=1.
    // Size-specific delay fields override the legacy field when supplied.
    private const double DefaultShieldDuration = 10000;
    private const double DefaultShieldDelayChance = 1;

    public static NpcRepairProfile Profile(IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        new(
            Cycle(
                attributes,
                DogmaAttributeId.EntityShieldBoostAmount,
                DogmaAttributeId.EntityShieldBoostDuration,
                DogmaAttributeId.EntityShieldBoostDelayChance,
                DogmaAttributeId.EntityShieldBoostDelayChanceSmall,
                DogmaAttributeId.EntityShieldBoostDelayChanceSmallMultiplier,
                DefaultShieldDuration,
                DefaultShieldDelayChance
            ),
            Cycle(
                attributes,
                DogmaAttributeId.EntityArmorRepairAmount,
                DogmaAttributeId.EntityArmorRepairDuration,
                DogmaAttributeId.EntityArmorRepairDelayChance,
                DogmaAttributeId.EntityArmorRepairDelayChanceSmall,
                DogmaAttributeId.EntityArmorRepairDelayChanceSmallMultiplier,
                0,
                0
            )
        );

    private static NpcRepairCycle? Cycle(
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        DogmaAttributeId amountId,
        DogmaAttributeId durationId,
        DogmaAttributeId chanceId,
        DogmaAttributeId smallChanceId,
        DogmaAttributeId smallMultiplierId,
        double defaultDuration,
        double defaultChance
    )
    {
        double amount = attributes.GetValueOrDefault(amountId);
        if (!double.IsFinite(amount) || amount < 0)
        {
            throw new InvalidDataException("Invalid NPC repair amount.");
        }
        if (amount == 0)
        {
            return null;
        }
        double duration = attributes.GetValueOrDefault(durationId, defaultDuration);
        double chance = attributes.GetValueOrDefault(chanceId, defaultChance);
        // These three contiguous catalog IDs represent small, medium and large.
        var sizes = Enumerable.Range(0, 3).Where(size => attributes.ContainsKey(smallChanceId + size)).ToArray();
        if (sizes.Length == 1)
        {
            int size = sizes[0];
            chance = attributes[smallChanceId + size] * attributes.GetValueOrDefault(smallMultiplierId + size, 1);
        }
        if (!double.IsFinite(duration) || duration <= 0 || !double.IsFinite(chance) || chance is < 0 or > 1)
        {
            throw new InvalidDataException("Invalid NPC repair duration or delay chance.");
        }
        long ticks = checked((long)Math.Ceiling(duration * TimeSpan.TicksPerMillisecond));
        return new(amount, ticks, chance);
    }

    public static NpcRepairResult Advance(
        NpcRepairProfile profile,
        NpcRepairState state,
        LiveHealth health,
        bool available,
        long now,
        Func<double> draw
    )
    {
        if (now < health.UpdatedAt)
        {
            return new(health, state);
        }
        if (!available || health.Remaining.Destroyed)
        {
            return new(health, state with { ShieldReadyAt = 0, ArmorReadyAt = 0 });
        }
        var current = health.At(now);
        var shield = Attempt(profile.Shield, state.ShieldReadyAt, current.Shield, health.Defense.Shield, now, draw);
        var armor = Attempt(profile.Armor, state.ArmorReadyAt, current.Armor, health.Defense.Armor, now, draw);
        bool repaired = shield.Amount > 0 || armor.Amount > 0;
        return new(
            repaired
                ? health with
                {
                    Remaining = current with { Shield = current.Shield + shield.Amount, Armor = current.Armor + armor.Amount },
                    UpdatedAt = now,
                    Revision = health.Revision + 1,
                    ArmorDamage = armor.Amount > 0 ? health.Defense.Armor - current.Armor - armor.Amount : health.ArmorDamage,
                }
                : health,
            new(shield.ReadyAt, armor.ReadyAt, repaired ? checked(state.Revision + 1) : state.Revision)
        );
    }

    private sealed record AttemptResult(long ReadyAt, double Amount);

    private static AttemptResult Attempt(NpcRepairCycle? cycle, long ready, double remaining, double capacity, long now, Func<double> draw)
    {
        if (cycle is null || remaining >= capacity)
        {
            return new(0, 0);
        }
        if (ready == 0)
        {
            return new(checked(now + cycle.DurationTicks), 0);
        }
        if (now < ready)
        {
            return new(ready, 0);
        }
        double roll = draw();
        if (!double.IsFinite(roll) || roll is < 0 or >= 1)
        {
            throw new InvalidDataException("Invalid NPC repair probability draw.");
        }
        double amount = roll >= cycle.DelayChance ? Math.Min(cycle.Amount, capacity - remaining) : 0;
        // One due attempt per observation; a delayed tick never replays missed cycles.
        return new(remaining + amount >= capacity ? 0 : checked(now + cycle.DurationTicks), amount);
    }
}
