using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Combat;

public sealed record LiveNpcTarget(long Id, int SystemId, SpacePosition Position, double Radius);

public static class LiveNpcAcquisitionRules
{
    public static IReadOnlyList<LiveAggression> Acquire(
        IReadOnlyList<LiveCombatNpc> npcs,
        IReadOnlyList<LiveNpcTarget> targets,
        IReadOnlySet<long> occupied,
        long now
    ) => Acquire(npcs, targets, occupied.Contains, now);

    public static IReadOnlyList<LiveAggression> Acquire(
        IReadOnlyList<LiveCombatNpc> npcs,
        IReadOnlyList<LiveNpcTarget> targets,
        Func<long, bool> occupied,
        long now
    )
    {
        if (npcs.Count == 0 || targets.Count == 0)
        {
            return [];
        }
        List<LiveAggression>? additions = null;
        foreach (var npc in npcs.OrderBy(n => n.Id))
        {
            if (
                npc.Role != NpcRole.Pirate
                || npc.Navigation != NpcNavigationState.Idle
                || occupied(npc.Id)
                || (
                    !npc.Attributes.TryGetValue(DogmaAttributeId.EntityAttackDelayMinimum, out double delay)
                    && npc.GroupId is not (NpcEncounterDefinitions.SentryGroup or NpcEncounterDefinitions.OverseerSentryGroup)
                )
            )
            {
                continue;
            }
            double detection = NpcPopulationRules.DetectionRange(npc.Attributes, npc.MissileRange, npc.AcquisitionRange);
            double maximumRange = Math.Min(detection, npc.Attributes.GetValueOrDefault(DogmaAttributeId.MaxTargetRange, double.MaxValue));
            double leash = NpcPopulationRules.Leash(detection);
            LiveNpcTarget? target = null;
            double closest = double.PositiveInfinity;
            foreach (var ship in targets)
            {
                if (ship.SystemId != npc.SystemId || !PlayerEwarRules.CanTarget(npc.IncomingElectronicWarfare, ship.Id))
                {
                    continue;
                }
                double range = Math.Max(0, ship.Position.DistanceTo(npc.Position) - npc.Radius - ship.Radius);
                if (
                    range <= maximumRange
                    && (target is null || range < closest || range == closest && ship.Id < target.Id)
                    && ship.Position.DistanceTo(npc.Home) <= leash
                )
                {
                    target = ship;
                    closest = range;
                }
            }
            if (target is null)
            {
                continue;
            }
            if (!double.IsFinite(delay) || delay < 0)
            {
                throw new InvalidDataException("Invalid NPC reaction delay.");
            }
            long ready = checked(now + (long)Math.Ceiling(delay * TimeSpan.TicksPerMillisecond));
            (additions ??= []).Add(new(npc.Id, target.Id, ready, ready, null));
        }
        return additions is null ? [] : additions;
    }
}
