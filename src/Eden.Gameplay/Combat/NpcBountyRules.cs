using Eden.Gameplay.Dogma;
using Eden.Gameplay.Market;

namespace Eden.Gameplay.Combat;

public static class NpcBountyRules
{
    public static long BaseCents(NpcRole role, IReadOnlyDictionary<DogmaAttributeId, double> attributes, NpcAnchorKind? anchor = null)
    {
        if (anchor is NpcAnchorKind kind && NpcPopulationRules.IsIncursionPatrol(kind))
        {
            return 0;
        }
        double bounty = NpcPopulationRules.AwardsBounty(role) ? attributes.GetValueOrDefault(DogmaAttributeId.EntityKillBounty) : 0;
        if (!double.IsFinite(bounty) || bounty < 0)
        {
            throw new InvalidDataException("Invalid NPC bounty.");
        }
        return checked((long)Math.Round(bounty * MarketDefinitions.CentsPerIsk, MidpointRounding.ToEven));
    }
}
