namespace Eden.Gameplay.Market;

public static class StandardMarketRules
{
    // Build 3662168 marketutil.const.rangeRegion; ESI names the same scope "region".
    public const int RegionRange = short.MaxValue;

    public static bool CanFill(int requested, int remaining, int minimumVolume) =>
        Math.Min(requested, remaining) >= Math.Min(minimumVolume, remaining);

    public static bool Reaches(int range, bool sameStation, int? jumps) =>
        sameStation || range == RegionRange || range >= 0 && jumps is int distance && distance <= range;
}
