using Eden.Gameplay.Skills;

namespace Eden.Gameplay.Characters;

public sealed record LiveLearningBooster(ActiveBooster Booster, int RemainingInjectionUses);

public sealed record LiveLearningState(
    ImplantSnapshot Implants,
    IReadOnlyList<LiveLearningBooster> Boosters,
    long BoosterRevision,
    long NextAlphaInjection,
    long InjectionRevision
)
{
    public BoosterSnapshot At(long now)
    {
        var active = Boosters.Where(b => b.Booster.ExpiresAt > now).Select(b => b.Booster).ToArray();
        return new(Array.AsReadOnly(active), BoosterRevision, BoosterRules.Effective(Implants.Attributes, active));
    }

    public SkillInjectorStatus InjectionsAt(long now) =>
        new(Boosters.Where(b => b.Booster.ExpiresAt > now).Sum(b => b.RemainingInjectionUses), NextAlphaInjection, InjectionRevision);
}
