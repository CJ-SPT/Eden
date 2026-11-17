namespace Eden.Gameplay.Flight;

public static class SiteWarpInRules
{
    private const double Clearance = 1;
    private const int MaximumClearancePasses = 64;

    private sealed record ObstacleInterval(double Start, double End);

    public static SpacePosition ResolveAnchor(
        long shipId,
        float shipRadius,
        SpacePosition origin,
        SpacePosition anchor,
        double range,
        IReadOnlyList<StaticSpaceObject> objects
    )
    {
        var direction = (origin - anchor).Unit;
        if (direction.Length == 0)
        {
            return anchor;
        }
        var landing = WarpCourse.LandingPoint(shipId, shipRadius, origin, anchor, range);
        List<ObstacleInterval> intervals = [];
        foreach (var body in objects.Where(o => o.Solid && o.Radius > 0 && o.Collision is null))
        {
            var delta = body.Position - landing;
            double along = delta.Dot(direction);
            double sidewaysSquared = Math.Max(0, delta.Dot(delta) - along * along);
            double radius = body.Radius + shipRadius + Clearance;
            if (sidewaysSquared > radius * radius)
            {
                continue;
            }
            double halfWidth = Math.Sqrt(radius * radius - sidewaysSquared);
            intervals.Add(new(along - halfWidth, along + halfWidth));
        }
        var ordered = intervals.OrderBy(interval => interval.Start).ToArray();
        var compounds = objects.Where(body => body.Solid && body.Collision is not null).ToArray();
        double offset = 0;
        bool settled = false;
        for (int pass = 0; pass < MaximumClearancePasses; pass++)
        {
            double previous = offset;
            foreach (var interval in ordered)
            {
                if (interval.Start > offset)
                {
                    break;
                }
                offset = Math.Max(offset, interval.End);
            }
            foreach (var body in compounds)
            {
                var local = landing + direction * offset - body.Position;
                var cleared = body.Collision!.ClearAlongRay(local, direction, shipRadius + Clearance);
                offset += (cleared - local).Dot(direction);
            }
            if (offset == previous)
            {
                settled = true;
                break;
            }
        }
        if (!settled)
        {
            throw new InvalidDataException("No clear landing point is available along the approach.");
        }
        return offset > 0 ? anchor + direction * offset : anchor;
    }
}
