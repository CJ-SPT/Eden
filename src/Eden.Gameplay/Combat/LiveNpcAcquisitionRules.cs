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
    )
    {
        List<LiveAggression> additions = [];
        foreach (var npc in npcs.OrderBy(n => n.Id))
        {
            if (
                npc.Role != NpcRole.Pirate
                || npc.Navigation != NpcNavigationState.Idle
                || occupied.Contains(npc.Id)
                || (
                    !npc.Attributes.TryGetValue(DogmaAttributeId.EntityAttackDelayMinimum, out double delay)
                    && npc.GroupId != NpcEncounterDefinitions.SentryGroup
                )
            )
            {
                continue;
            }
            var target = targets
                .Where(s => s.SystemId == npc.SystemId)
                .Select(s => (Ship: s, Range: Math.Max(0, s.Position.DistanceTo(npc.Position) - npc.Radius - s.Radius)))
                .Where(t =>
                    PlayerEwarRules.CanTarget(npc.IncomingElectronicWarfare, t.Ship.Id)
                    && t.Range <= npc.Attributes.GetValueOrDefault(DogmaAttributeId.MaxTargetRange, double.MaxValue)
                    && t.Range <= NpcPopulationRules.DetectionRange(npc.Attributes, npc.MissileRange, npc.AcquisitionRange)
                    && t.Ship.Position.DistanceTo(npc.Home)
                        <= NpcPopulationRules.Leash(npc.Attributes, npc.MissileRange, npc.AcquisitionRange)
                )
                .OrderBy(t => t.Range)
                .ThenBy(t => t.Ship.Id)
                .FirstOrDefault();
            if (target.Ship is null)
            {
                continue;
            }
            if (!double.IsFinite(delay) || delay < 0)
            {
                throw new InvalidDataException("Invalid NPC reaction delay.");
            }
            long ready = checked(now + (long)Math.Ceiling(delay * TimeSpan.TicksPerMillisecond));
            additions.Add(new(npc.Id, target.Ship.Id, ready, ready, null));
        }
        return additions;
    }
}
