using Eden.Gameplay.Combat;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Capsuleers;

public static class CapsuleerReplacementRules
{
    public const int OrderDays = 7;
    public const int StockRetrySeconds = 300;
    public static readonly TimeSpan BidWindow = TimeSpan.FromMinutes(30);
    private const decimal ReferencePriceMultiplier = 2m;

    public static IReadOnlyList<ReplacementPart> MerlinParts { get; } =
        Array.AsReadOnly<ReplacementPart>([
            new((int)TechOneHullType.Merlin, 1),
            new((int)CombatWeaponType.GatlingRailgun75mm, 2),
            // The previous fixed fit bought two full 200-round magazines of Iron Charge S.
            new(CapsuleerAmmunitionSupplyRules.ChargeType, 400),
        ]);

    public static long LimitPrice(long referenceCents) =>
        (long)Math.Clamp(referenceCents * ReferencePriceMultiplier, 0, MarketDefinitions.MaximumOrderPrice * MarketDefinitions.CentsPerIsk);
}

public sealed record ReplacementPart(int TypeId, int Quantity);

public sealed record ReplacementMagazine(int Slot, int TypeId, int Quantity);

public sealed record ReplacementKit(
    int HullTypeId,
    FittingSlot[] Slots,
    IReadOnlySet<int> OnlineSlots,
    SavedFittingItem[] Supplies,
    ReplacementMagazine[] Magazines
)
{
    public ReplacementPart[] Parts() =>
        new[] { new ReplacementPart(HullTypeId, 1) }
            .Concat(Slots.Select(slot => new ReplacementPart(slot.TypeId, 1)))
            .Concat(Supplies.Select(item => new ReplacementPart(item.TypeId, item.Quantity)))
            .Concat(Magazines.Select(magazine => new ReplacementPart(magazine.TypeId, magazine.Quantity)))
            .GroupBy(part => part.TypeId)
            .Select(group => new ReplacementPart(group.Key, group.Sum(part => part.Quantity)))
            .ToArray();
}

public sealed record ReplacementResult(bool Boarded, bool MarketChanged, long? OrderDeadline, long[]? OrderIds = null);
