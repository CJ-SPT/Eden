using System.Collections.Immutable;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Agents;

public sealed record MissionSpawnAnchor(
    long ObjectId,
    SpacePosition Position,
    double AcquisitionRange,
    bool Initial,
    bool Reinforcement,
    int? SentryTypeId = null
);

public sealed record MissionSpawnMember(int TypeId, int Group, double Range, bool Sentry);

public sealed record MissionSpawnPlacement(SpacePosition Position, double AcquisitionRange);

public static class MissionSpawnRules
{
    private const int MembersPerRing = 8;

    public static ImmutableArray<MissionSpawnPlacement> Place(
        bool initial,
        IReadOnlyList<MissionSpawnMember> members,
        ImmutableArray<MissionSpawnAnchor> anchors
    )
    {
        var mobile = anchors.Where(a => a.SentryTypeId is null).ToArray();
        var candidates = mobile.Where(a => initial ? a.Initial : a.Reinforcement).ToArray();
        if (candidates.Length == 0)
        {
            candidates = mobile;
        }
        var sentries = anchors
            .Where(a => a.SentryTypeId.HasValue && (initial ? a.Initial : a.Reinforcement))
            .ToLookup(a => a.SentryTypeId!.Value);
        Dictionary<int, MissionSpawnAnchor?> groups = [];
        Dictionary<long, int> anchorSlots = [];
        Dictionary<int, int> sentrySlots = [];
        var result = ImmutableArray.CreateBuilder<MissionSpawnPlacement>(members.Count);
        for (int slot = 0; slot < members.Count; slot++)
        {
            var member = members[slot];
            if (member.Group < 0 || !double.IsFinite(member.Range) || member.Range < 0)
            {
                throw new InvalidDataException("Invalid mission spawn group or range.");
            }
            if (member.Sentry)
            {
                int index = sentrySlots.GetValueOrDefault(member.TypeId);
                sentrySlots[member.TypeId] = index + 1;
                var anchor = sentries[member.TypeId].ElementAtOrDefault(index);
                if (anchor is not null)
                {
                    result.Add(new(anchor.Position, anchor.AcquisitionRange));
                    continue;
                }
            }
            else
            {
                if (!groups.TryGetValue(member.Group, out var anchor))
                {
                    anchor = Nearest(candidates, member);
                    groups[member.Group] = anchor;
                }
                if (anchor is not null)
                {
                    int index = anchorSlots.GetValueOrDefault(anchor.ObjectId);
                    anchorSlots[anchor.ObjectId] = index + 1;
                    result.Add(new(anchor.Position + Offset(index), anchor.AcquisitionRange));
                    continue;
                }
            }
            result.Add(new(new SpacePosition(member.Range, 0, 0) + Offset(slot), NpcEncounterDefinitions.VisibilityRange));
        }
        return result.MoveToImmutable();
    }

    private static MissionSpawnAnchor? Nearest(MissionSpawnAnchor[] anchors, MissionSpawnMember member)
    {
        if (anchors.Length == 0)
        {
            return null;
        }
        // Hidden grouping IDs do not expose their rosters. Report ranges select
        // the nearest native anchors; similarly distant anchors share groups in
        // stable object-ID order. This association is an explicit Eden policy.
        var ordered = anchors.OrderBy(a => Math.Abs(a.Position.Length - member.Range)).ThenBy(a => a.ObjectId).ToArray();
        double limit = Math.Abs(ordered[0].Position.Length - member.Range) + NpcSpawnRules.MemberSpacing;
        var nearby = ordered.Where(a => Math.Abs(a.Position.Length - member.Range) <= limit).OrderBy(a => a.ObjectId).ToArray();
        return nearby[member.Group % nearby.Length];
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
