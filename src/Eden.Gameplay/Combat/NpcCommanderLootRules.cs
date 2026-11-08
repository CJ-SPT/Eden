namespace Eden.Gameplay.Combat;

// Catalog type IDs from build 3662168. The sparse drop pool and probabilities
// are Eden policy because public encounter reports do not publish exact loot odds.
public enum NpcCommanderRewardType
{
    DominationCopperTag = 17222,
    DominationGyrostabilizer = 13939,
    DominationEmpSmall = 20767,
    DarkBloodCopperTag = 17233,
    DarkBloodSmallArmorRepairer = 13964,
    DarkBloodMultifrequencySmall = 21270,
    DreadGuristasCopperTag = 17243,
    DreadGuristasBallisticControl = 13937,
    DreadGuristasScourgeLightMissile = 27365,
    TrueSanshaCopperTag = 17254,
    TrueSanshaSmallArmorRepairer = 13963,
    TrueSanshasMultifrequencySmall = 20863,
    ShadowSerpentisCopperTag = 17264,
    ShadowSerpentisMagneticFieldStabilizer = 13945,
    ShadowAntimatterSmall = 20040,
    EliteDroneAi = 21815,
    SentientOmnidirectionalTrackingLink = 33826,
    DroneCapillaryFluid = 28362,
}

public static class NpcCommanderLootRules
{
    private sealed record Reward(NpcCommanderRewardType Tag, NpcCommanderRewardType Ammunition, NpcCommanderRewardType Module);

    private const int ModuleRollOutcomes = 4;
    private const int AmmunitionQuantity = 1000;

    public static IReadOnlyList<NpcLootDrop> Roll(NpcPirateFaction faction, uint seed)
    {
        var reward = faction switch
        {
            NpcPirateFaction.Angels => new Reward(
                NpcCommanderRewardType.DominationCopperTag,
                NpcCommanderRewardType.DominationEmpSmall,
                NpcCommanderRewardType.DominationGyrostabilizer
            ),
            NpcPirateFaction.BloodRaiders => new Reward(
                NpcCommanderRewardType.DarkBloodCopperTag,
                NpcCommanderRewardType.DarkBloodMultifrequencySmall,
                NpcCommanderRewardType.DarkBloodSmallArmorRepairer
            ),
            NpcPirateFaction.Guristas => new Reward(
                NpcCommanderRewardType.DreadGuristasCopperTag,
                NpcCommanderRewardType.DreadGuristasScourgeLightMissile,
                NpcCommanderRewardType.DreadGuristasBallisticControl
            ),
            NpcPirateFaction.Sansha => new Reward(
                NpcCommanderRewardType.TrueSanshaCopperTag,
                NpcCommanderRewardType.TrueSanshasMultifrequencySmall,
                NpcCommanderRewardType.TrueSanshaSmallArmorRepairer
            ),
            NpcPirateFaction.Serpentis => new Reward(
                NpcCommanderRewardType.ShadowSerpentisCopperTag,
                NpcCommanderRewardType.ShadowAntimatterSmall,
                NpcCommanderRewardType.ShadowSerpentisMagneticFieldStabilizer
            ),
            NpcPirateFaction.RogueDrones => new Reward(
                NpcCommanderRewardType.EliteDroneAi,
                NpcCommanderRewardType.DroneCapillaryFluid,
                NpcCommanderRewardType.SentientOmnidirectionalTrackingLink
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(faction)),
        };
        List<NpcLootDrop> drops =
        [
            new((int)reward.Tag, 1),
            new((int)reward.Ammunition, faction == NpcPirateFaction.RogueDrones ? 1 : AmmunitionQuantity),
        ];
        if (seed % ModuleRollOutcomes == 0)
        {
            drops.Add(new((int)reward.Module, 1));
        }
        return drops;
    }
}
