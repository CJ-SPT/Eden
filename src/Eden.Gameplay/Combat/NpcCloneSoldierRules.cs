using System.Collections.Frozen;
using Eden.Gameplay.Characters;

namespace Eden.Gameplay.Combat;

public static class NpcCloneSoldierRules
{
    public const int TagGroup = 1206;
    private const uint ReplacementDenominator = 20;

    // Ascending displayed security from 0.1 to 0.4, using the pinned catalog IDs.
    private static readonly SecurityTagType[] s_tags =
    [
        SecurityTagType.CloneSoldierNegotiator,
        SecurityTagType.CloneSoldierTransporter,
        SecurityTagType.CloneSoldierRecruiter,
        SecurityTagType.CloneSoldierTrainer,
    ];
    private static readonly FrozenDictionary<NpcPirateFaction, NpcCloneSoldierType[]> s_factions = new Dictionary<
        NpcPirateFaction,
        NpcCloneSoldierType[]
    >
    {
        [NpcPirateFaction.Angels] =
        [
            NpcCloneSoldierType.AngelNegotiator,
            NpcCloneSoldierType.AngelTransporter,
            NpcCloneSoldierType.AngelRecruiter,
            NpcCloneSoldierType.AngelTrainer,
        ],
        [NpcPirateFaction.BloodRaiders] =
        [
            NpcCloneSoldierType.BloodNegotiator,
            NpcCloneSoldierType.BloodTransporter,
            NpcCloneSoldierType.BloodRecruiter,
            NpcCloneSoldierType.BloodTrainer,
        ],
        [NpcPirateFaction.Guristas] =
        [
            NpcCloneSoldierType.GuristasNegotiator,
            NpcCloneSoldierType.GuristasTransporter,
            NpcCloneSoldierType.GuristasRecruiter,
            NpcCloneSoldierType.GuristasTrainer,
        ],
        [NpcPirateFaction.Sansha] =
        [
            NpcCloneSoldierType.SanshaNegotiator,
            NpcCloneSoldierType.SanshaTransporter,
            NpcCloneSoldierType.SanshaRecruiter,
            NpcCloneSoldierType.SanshaTrainer,
        ],
        [NpcPirateFaction.Serpentis] =
        [
            NpcCloneSoldierType.SerpentisNegotiator,
            NpcCloneSoldierType.SerpentisTransporter,
            NpcCloneSoldierType.SerpentisRecruiter,
            NpcCloneSoldierType.SerpentisTrainer,
        ],
    }.ToFrozenDictionary();
    private static readonly FrozenDictionary<int, SecurityTagType> s_tagsByHull = s_factions
        .Values.SelectMany(hulls => hulls.Select((hull, index) => KeyValuePair.Create((int)hull, s_tags[index])))
        .ToFrozenDictionary();

    public static SecurityTagType? TagFor(int typeId) => s_tagsByHull.TryGetValue(typeId, out var tag) ? tag : null;

    public static NpcCloneSoldierType? Select(NpcPirateFaction faction, double security, uint seed)
    {
        // The low bit determines initial belt occupancy. Use the remaining bits for this independent draw.
        if (security is not (> 0 and < CrimewatchRules.HighSecurity) || (seed >> 1) % ReplacementDenominator != 0)
        {
            return null;
        }
        int band = Math.Clamp((int)Math.Round(security * 10, MidpointRounding.AwayFromZero), 1, 4);
        return s_factions.TryGetValue(faction, out var hulls) ? hulls[band - 1] : null;
    }
}
