namespace Eden.Gameplay.Agents;

public sealed record MissionChainPart(int RootMissionId, int PreviousMissionId, int NextMissionId);

public sealed record MissionChainNode(int MissionId, int Level, bool Storyline, MissionChainPart Chain);

public static class MissionChainRules
{
    public static void Validate(IReadOnlyList<MissionChainNode> nodes)
    {
        if (nodes.Select(n => n.MissionId).Distinct().Count() != nodes.Count)
        {
            throw new InvalidDataException("Mission chains contain duplicate mission identities.");
        }
        foreach (var family in nodes.GroupBy(n => n.Chain.RootMissionId))
        {
            var parts = family.ToDictionary(n => n.MissionId);
            if (parts.Count < 2 || !parts.TryGetValue(family.Key, out var root) || root.Chain.PreviousMissionId != 0)
            {
                throw new InvalidDataException("Mission chains require a root and a complete sequence.");
            }
            HashSet<int> visited = [];
            int previous = 0;
            int current = root.MissionId;
            while (current != 0)
            {
                if (
                    !parts.TryGetValue(current, out var part)
                    || current <= 0
                    || !visited.Add(current)
                    || part.Chain.PreviousMissionId != previous
                    || part.Level != root.Level
                    || part.Storyline != root.Storyline
                    || part.Level is < 1 or > MissionRules.MaximumAgentLevel
                )
                {
                    throw new InvalidDataException("Mission chains must form complete, ordered, same-level sequences without cycles.");
                }
                previous = current;
                current = part.Chain.NextMissionId;
            }
            if (visited.Count != parts.Count)
            {
                throw new InvalidDataException("Mission chains contain unreachable parts.");
            }
        }
    }

    public static void RequirePredecessor(MissionChainPart part, int? previousMissionId, MissionState? previousState)
    {
        if (part.PreviousMissionId != 0 && (previousMissionId != part.PreviousMissionId || previousState != MissionState.Completed))
        {
            throw new InvalidDataException("Complete the preceding mission with this agent before accepting this chain part.");
        }
    }
}
