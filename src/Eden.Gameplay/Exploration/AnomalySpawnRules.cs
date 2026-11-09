using System.Collections.Immutable;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Exploration;

public sealed record AnomalySpawnAnchor(
    long ObjectId,
    SpacePosition Position,
    double AcquisitionRange,
    bool Initial,
    bool Reinforcement,
    int? SentryTypeId = null
);

public sealed record AnomalySpawnPlacement(SpacePosition Position, double AcquisitionRange);

public static class AnomalySpawnRules
{
    private const int MembersPerRing = 8;

    public static ImmutableArray<AnomalySpawnPlacement> Place(
        int wave,
        IReadOnlyList<AnomalySpawnMember> members,
        ImmutableArray<AnomalySpawnAnchor> anchors,
        AnomalySpawnPlacement fallback
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wave);
        var mobile = anchors.Where(a => a.SentryTypeId is null).ToArray();
        var candidates = mobile.Where(a => wave == 0 ? a.Initial : a.Reinforcement).ToArray();
        if (candidates.Length == 0)
        {
            candidates = mobile;
        }
        var sentries = anchors
            .Where(a => a.SentryTypeId.HasValue && (wave == 0 ? a.Initial : a.Reinforcement))
            .ToLookup(a => a.SentryTypeId!.Value);
        Dictionary<int, int> sentrySlots = [];
        Dictionary<long, int> mobileSlots = [];
        var result = ImmutableArray.CreateBuilder<AnomalySpawnPlacement>(members.Count);
        for (int slot = 0; slot < members.Count; slot++)
        {
            var member = members[slot];
            if (member.Sentry)
            {
                int sentrySlot = sentrySlots.GetValueOrDefault(member.TypeId);
                sentrySlots[member.TypeId] = sentrySlot + 1;
                var anchor = sentries[member.TypeId].ElementAtOrDefault(sentrySlot);
                if (anchor is not null)
                {
                    result.Add(new(anchor.Position, anchor.AcquisitionRange));
                    continue;
                }
            }
            else if (candidates.Length > 0)
            {
                // Native grouping IDs omit their rosters. Rotating report groups over
                // eligible anchors is an explicit Eden policy, not a recovered binding.
                int index = (member.Group + Math.Max(0, wave - 1)) % candidates.Length;
                var anchor = candidates[index];
                int memberSlot = mobileSlots.GetValueOrDefault(anchor.ObjectId);
                mobileSlots[anchor.ObjectId] = memberSlot + 1;
                result.Add(new(anchor.Position + Offset(memberSlot), anchor.AcquisitionRange));
                continue;
            }
            // Unmatched reported batteries retain a visible fallback rather than
            // duplicating a fixed tower's position or inventing a catalog identity.
            result.Add(new(fallback.Position + Offset(slot), fallback.AcquisitionRange));
        }
        return result.MoveToImmutable();
    }

    private static SpacePosition Offset(int slot)
    {
        if (slot == 0)
        {
            return SpacePosition.Zero;
        }
        int ring = (slot - 1) / MembersPerRing + 1;
        double angle = (slot - 1) % MembersPerRing * Math.Tau / MembersPerRing;
        double radius = ring * NpcSpawnRules.MemberSpacing;
        return new(Math.Cos(angle) * radius, 0, Math.Sin(angle) * radius);
    }
}
