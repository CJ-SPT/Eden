namespace Eden.Gameplay.Flight;

public static class SiteWarpInRules
{
    private const double Clearance = 1;

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
        foreach (var body in objects.Where(o => o.Solid && o.Radius > 0))
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
        double offset = 0;
        foreach (var interval in intervals.OrderBy(i => i.Start))
        {
            if (interval.Start > offset)
            {
                break;
            }
            offset = Math.Max(offset, interval.End);
        }
        return offset > 0 ? anchor + direction * offset : anchor;
    }
}
