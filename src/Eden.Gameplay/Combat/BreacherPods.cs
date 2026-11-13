namespace Eden.Gameplay.Combat;

public sealed record BreacherPayload(double DurationMilliseconds, double MaximumDamage, double MaximumPercent, long? AttachedAt = null)
{
    public double Damage(HullDefense defense, double resistance = 1) =>
        Math.Min(MaximumDamage, (defense.Shield + defense.Armor + defense.Structure) * MaximumPercent / 100) * resistance;
}

public enum BreacherSkill
{
    LauncherOperation = 86260,
    CloneEfficacy = 86261,
    CloneLongevity = 86262,
    Projection = 86263,
    RapidFiring = 86264,
}

public static class BreacherRules
{
    public const int CounterType = 95414;
    public const string CounterGraphic = "effects.vanguardDamageControlModule";
    public const int TargetTypeList = 792;
    public const long Tick = TimeSpan.TicksPerSecond;
}
