using Eden.Gameplay.Market;

namespace Eden.Gameplay.Combat;

public sealed record KillRight(long Id, long OwnerId, long TargetId, long ExpiresAt, long? PriceCents, long? RestrictedTo);

public sealed record KillRightPresence(int SystemId, long ShipId, IReadOnlySet<long> VisibleShips);

public enum KillRightActivationResult
{
    Activated,
    Unavailable,
    Expired,
    NotForSale,
    PriceChanged,
    TargetNotVisible,
}

public interface IPilotKillRights
{
    IReadOnlyList<KillRight> Mine(long pilotId);
    IReadOnlyList<KillRight> Available(long pilotId, IReadOnlyList<long> targets);
    KillRight ListForSale(long pilotId, long id, long priceCents, long? restrictedTo);
    void CancelSale(long pilotId, long id, long targetId);
    KillRightActivationResult Activate(long pilotId, long id, KillRightPresence presence, long? quotedPriceCents = null);
}

public static class KillRightRules
{
    // Build 3662168 killRights uses appConst.MAX_BOUNTY_AMOUNT.
    public const long MaximumPriceIsk = 100_000_000_000;
    public const long MaximumPriceCents = MaximumPriceIsk * MarketDefinitions.CentsPerIsk;
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public static bool CanBuy(KillRight right, long pilotId, int corporationId, int? allianceId) =>
        right.PriceCents.HasValue
        && (
            right.RestrictedTo is null
            || right.RestrictedTo == pilotId
            || right.RestrictedTo == corporationId
            || right.RestrictedTo == allianceId
        );
}
