using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Capsuleers;

// Ordinary haulers in build 3662168. Faction variants and capitals require separate admission.
public enum FreightHull
{
    Badger = 648,
    Tayra = 649,
    Nereus = 650,
    Hoarder = 651,
    Mammoth = 652,
    Wreathe = 653,
    Kryos = 654,
    Epithal = 655,
    Miasmos = 656,
    IteronMarkV = 657,
    Bestower = 1944,
    Sigil = 19744,
}

public enum FreightKind
{
    Delivery,
    Bulk,
    Ammunition,
    Minerals,
    Planetary,
    Ore,
    Courier,
}

public sealed record FreightItem(int TypeId, int Quantity);

public sealed record FreightStack(long ItemId, int TypeId, int Quantity);

public sealed record FreightManifest(long Sequence, int Destination, FreightStack[] Stacks);

public sealed record FreightCommodity(int TypeId, double Volume, double UnitValue);

public static class FreightRules
{
    public const int LegacyOreUnits = 100;

    public static FreightKind Kind(FreightHull hull, ulong seed) =>
        hull switch
        {
            FreightHull.Miasmos => FreightKind.Ore,
            FreightHull.Hoarder => FreightKind.Ammunition,
            FreightHull.Kryos => FreightKind.Minerals,
            FreightHull.Epithal => FreightKind.Planetary,
            FreightHull.Tayra or FreightHull.Bestower or FreightHull.Mammoth or FreightHull.IteronMarkV => FreightKind.Bulk,
            _ => seed % 12 == 0 ? FreightKind.Courier : FreightKind.Delivery,
        };

    public static InventoryFlag PreferredHold(FreightKind kind) =>
        kind switch
        {
            FreightKind.Ammunition => InventoryFlag.AmmoHold,
            FreightKind.Minerals => InventoryFlag.MineralHold,
            FreightKind.Planetary => InventoryFlag.PlanetaryCommoditiesHold,
            FreightKind.Ore => InventoryFlag.GeneralMiningHold,
            _ => InventoryFlag.Cargo,
        };

    public static FreightItem[] Load(ulong seed, long sequence, FreightKind kind, IReadOnlyList<FreightCommodity> goods, double free)
    {
        if (goods.Count == 0 || !double.IsFinite(free) || free <= 0)
        {
            return [];
        }
        ulong draw = TravelerRoutineRules.Mix(seed + unchecked((ulong)sequence));
        if (sequence > 0 && draw % 7 == 0)
        {
            return [];
        }
        // The manifest budget is a supply policy in ISK; no market lookup or wallet mutation.
        double budget = (kind == FreightKind.Courier ? 4_000_000 : 750_000) * (.2 + draw % 81 / 100d);
        double volume = free * (.08 + draw % 83 / 100d);
        int lines = Math.Min(goods.Count, 1 + (int)(draw % 4));
        int start = (int)(TravelerRoutineRules.Mix(draw) % (ulong)goods.Count);
        List<FreightItem> load = [];
        for (int offset = 0; offset < goods.Count && load.Count < lines; offset++)
        {
            var item = goods[(start + offset) % goods.Count];
            double share = 1d / (lines - load.Count);
            int units = (int)Math.Min(int.MaxValue, Math.Floor(Math.Min(volume / item.Volume, budget / item.UnitValue) * share));
            if (units > 0)
            {
                load.Add(new(item.TypeId, units));
                budget -= units * item.UnitValue;
                volume -= units * item.Volume;
            }
        }
        return load.ToArray();
    }
}
