using System.Collections.Immutable;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Agents;

public static partial class MissionSpawnRules
{
    // Leave room for float ball radii and solar-system coordinate rounding.
    private const double SpawnClearance = 10;

    private sealed record SpawnBody(SpacePosition Position, float Radius);

    private sealed record SpawnInterval(double Start, double End);

    private static ImmutableArray<MissionSpawnPlacement> ClearScenery(
        ImmutableArray<MissionSpawnPlacement> placements,
        IReadOnlyList<MissionSpawnMember> members,
        IReadOnlyList<StaticSpaceObject>? obstacles
    )
    {
        if (obstacles is null || obstacles.Count == 0)
        {
            return placements;
        }
        var bodies = obstacles.Where(o => o.Solid).OrderBy(o => o.Id).Select(o => new SpawnBody(o.Position, o.Radius)).ToList();
        // Fixed batteries retain their individually authored positions. Mobile NPCs
        // must also avoid those batteries and earlier members of this wave.
        for (int index = 0; index < members.Count; index++)
        {
            if (members[index].Sentry)
            {
                bodies.Add(new(placements[index].Position, members[index].Radius));
            }
        }
        var result = placements.ToBuilder();
        for (int index = 0; index < members.Count; index++)
        {
            var member = members[index];
            if (member.Sentry)
            {
                continue;
            }
            var position = placements[index].Position;
            var overlap = bodies.FirstOrDefault(b => position.DistanceTo(b.Position) <= (double)member.Radius + b.Radius + SpawnClearance);
            if (overlap is not null)
            {
                var radial = position - overlap.Position;
                SpacePosition[] directions =
                [
                    radial.Length > 0 ? radial.Unit : new(1, 0, 0),
                    new(1, 0, 0),
                    new(-1, 0, 0),
                    new(0, 1, 0),
                    new(0, -1, 0),
                    new(0, 0, 1),
                    new(0, 0, -1),
                ];
                var offset = directions
                    .Select(direction => direction * EscapeDistance(position, member.Radius, direction, bodies))
                    .OrderBy(offset => offset.Length)
                    .First();
                position += offset;
                result[index] = placements[index] with { Position = position };
            }
            bodies.Add(new(position, member.Radius));
        }
        return result.ToImmutable();
    }

    private static double EscapeDistance(SpacePosition origin, float radius, SpacePosition direction, IReadOnlyList<SpawnBody> bodies)
    {
        List<SpawnInterval> intervals = [];
        foreach (var body in bodies)
        {
            var relative = body.Position - origin;
            double along = relative.Dot(direction);
            double combined = (double)radius + body.Radius + SpawnClearance;
            double perpendicularSquared = Math.Max(0, relative.Dot(relative) - along * along);
            if (perpendicularSquared > combined * combined)
            {
                continue;
            }
            double half = Math.Sqrt(combined * combined - perpendicularSquared);
            if (along + half >= 0)
            {
                intervals.Add(new(along - half, along + half));
            }
        }
        // Sweep the union of forbidden ray intervals. Unlike repeated radial pushes,
        // this terminates even when scenery spheres overlap or enclose one another.
        double distance = 0;
        foreach (var interval in intervals.OrderBy(i => i.Start))
        {
            if (interval.Start > distance)
            {
                break;
            }
            distance = Math.Max(distance, interval.End + SpawnClearance);
        }
        return distance;
    }
}
