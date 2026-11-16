namespace Eden.Gameplay.Sovereignty;

public sealed record SovereigntyClaim(int SystemId, long HubId, int CorporationId, int AllianceId, long ClaimedAt);

public sealed record SovereigntySystem(int SystemId, int RegionId, double Security, long? FactionId, bool HubAnchoringAllowed);

public sealed record SovereigntyIndices(int Military, int Industrial, int Strategic);

public interface ISovereignty
{
    SovereigntyClaim? Read(int systemId);
    IReadOnlyList<SovereigntyClaim> List(int allianceId);
    SovereigntySystem? System(int systemId);
}

public static class SovereigntyRules
{
    // Build 3662168 map identities. Wormhole IDs start at 31,000,000; these
    // special known-space regions and Zarzakh do not permit player sovereignty.
    private const int FirstKnownSpaceSystem = 30000000;
    private const int FirstWormholeSystem = 31000000;
    private const int ZarzakhSystem = 30100000;
    private const int UuaF4Region = 10000004;
    private const int J7hzFRegion = 10000017;
    private const int A821ARegion = 10000019;
    private const int PochvenRegion = 10000070;
    public const int HubGroupId = 1012;
    public const int SovereigntyCategoryId = 40;
    public const int MaximumIndex = 5;
    public const decimal MaximumDefenseMultiplier = 6m;
    private const decimal BaseDefenseMultiplier = 1m;
    private const decimal CapitalDefenseBonus = 2m;

    // CCP's strategic hold thresholds and ADM table, also described by UniWiki.
    private static readonly int[] s_strategicDays = [7, 21, 35, 65, 100];
    private static readonly decimal[] s_activityBonuses = [0m, 0.6m, 1.2m, 1.7m, 2.1m, 2.5m];
    private static readonly decimal[] s_strategicBonuses = [0m, 0.4m, 0.6m, 0.8m, 0.9m, 1m];

    public static bool CanClaim(SovereigntySystem system) =>
        system.SystemId is >= FirstKnownSpaceSystem and < FirstWormholeSystem
        && system.SystemId != ZarzakhSystem
        && system.RegionId > 0
        && system.RegionId is not (UuaF4Region or J7hzFRegion or A821ARegion or PochvenRegion)
        && double.IsFinite(system.Security)
        && system.Security <= 0
        && system.FactionId is null or 0
        && system.HubAnchoringAllowed;

    public static int StrategicIndex(long claimedAt, long now)
    {
        if (claimedAt <= 0 || now < 0)
        {
            throw new InvalidDataException("Strategic age requires valid saved claim and current times.");
        }
        // A backwards clock cannot grant age or underflow the saved timestamp.
        long elapsed = now > claimedAt ? now - claimedAt : 0;
        return s_strategicDays.Count(days => elapsed >= days * TimeSpan.TicksPerDay);
    }

    public static decimal DefenseMultiplier(SovereigntyIndices indices, bool capital = false)
    {
        if (
            indices.Military is < 0 or > MaximumIndex
            || indices.Industrial is < 0 or > MaximumIndex
            || indices.Strategic is < 0 or > MaximumIndex
        )
        {
            throw new InvalidDataException("Sovereignty indices must be between zero and five.");
        }
        return Math.Min(
            MaximumDefenseMultiplier,
            BaseDefenseMultiplier
                + s_activityBonuses[indices.Military]
                + s_activityBonuses[indices.Industrial]
                + s_strategicBonuses[indices.Strategic]
                + (capital ? CapitalDefenseBonus : 0)
        );
    }
}
