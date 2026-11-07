using Eden.Gameplay.Content;

namespace Eden.Gameplay.Combat;

// Pinned build 3662168 clone-soldier types share ordinary asteroid cruiser
// groups, despite being special tag encounters. Group membership alone cannot
// admit them into the baseline regional population.
public enum NpcCloneSoldierType
{
    AngelTrainer = 33132,
    GuristasTrainer = 33134,
    BloodTrainer = 33135,
    SanshaTrainer = 33136,
    SerpentisTrainer = 33137,
    SanshaRecruiter = 33142,
    AngelRecruiter = 33143,
    BloodRecruiter = 33144,
    GuristasRecruiter = 33145,
    SerpentisRecruiter = 33146,
    SanshaTransporter = 33163,
    SerpentisTransporter = 33164,
    BloodTransporter = 33172,
    AngelTransporter = 33173,
    GuristasTransporter = 33174,
    AngelNegotiator = 33175,
    BloodNegotiator = 33182,
    SanshaNegotiator = 33183,
    SerpentisNegotiator = 33185,
    GuristasNegotiator = 33189,
}

// These development assets also occupy ordinary asteroid pirate groups in
// build 3662168. Their IDs come from the pinned client/types catalog.
public enum NpcDevelopmentType
{
    TestDrainer = 24630,
    TestAttacker = 25806,
}

public static class NpcSpecialTypes
{
    public static IReadOnlySet<int> Excluded => BuildContentPolicy.Current.Npcs.ExcludedTypes;
}
