using Eden.Gameplay.Combat;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Capsuleers;

public static class CapsuleerReplacementRules
{
    public const int HullType = (int)TechOneHullType.Merlin;
    public const int OrderDays = 7;
    private const int Railgun = (int)CombatWeaponType.GatlingRailgun75mm;

    // A pinned 75mm Gatling Rail I holds 200 units of Iron Charge S.
    private const int RoundsPerMagazine = 200;
    private const decimal ReferencePriceMultiplier = 2m;

    public static IReadOnlyList<FittingSlot> Slots { get; } =
        Array.AsReadOnly([new((int)InventoryFlag.HighSlot0, Railgun), new FittingSlot((int)InventoryFlag.HighSlot1, Railgun)]);

    public static IReadOnlyList<SavedFittingItem> Supplies { get; } =
        Array.AsReadOnly([
            new SavedFittingItem(CapsuleerAmmunitionSupplyRules.ChargeType, InventoryFlag.Cargo, Slots.Count * RoundsPerMagazine),
        ]);

    public static IReadOnlyList<ReplacementPart> Parts { get; } =
        Array.AsReadOnly([new ReplacementPart(HullType, 1), new(Railgun, Slots.Count), new(Supplies[0].TypeId, Supplies[0].Quantity)]);

    public static long LimitPrice(long referenceCents) =>
        (long)Math.Clamp(referenceCents * ReferencePriceMultiplier, 0, MarketDefinitions.MaximumOrderPrice * MarketDefinitions.CentsPerIsk);
}

public sealed record ReplacementPart(int TypeId, int Quantity);
