namespace Eden.Gameplay.Capsuleers;

public enum CapsuleerWorkKind
{
    Industry,
    Hunt,
    Routine,
}

public static class CapsuleerWorkRules
{
    public const int MinimumHuntingReplacements = 2;
    public static readonly TimeSpan EarnedHuntRetry = TimeSpan.FromMinutes(5);

    public static CapsuleerWorkKind Choose(bool industryDemand, bool canHunt, int readyReplacements)
    {
        if (industryDemand)
        {
            return CapsuleerWorkKind.Industry;
        }

        return canHunt && readyReplacements >= MinimumHuntingReplacements ? CapsuleerWorkKind.Hunt : CapsuleerWorkKind.Routine;
    }
}
