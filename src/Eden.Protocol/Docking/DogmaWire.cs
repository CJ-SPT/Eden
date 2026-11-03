using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.Docking;

internal abstract record DogmaItemKey : BlueModel
{
    public abstract override object ToBlue();

    internal sealed record Item(long Id) : DogmaItemKey
    {
        public override object ToBlue() => Id;
    }

    internal sealed record Charge(long ShipId, int FlagId, int TypeId) : DogmaItemKey
    {
        public override object ToBlue() => new object?[] { ShipId, FlagId, TypeId };
    }
}

internal sealed record DogmaActiveEffect(
    long ModuleId,
    long PilotId,
    long ShipId,
    long? TargetId,
    DogmaEffect Effect,
    long StartTime,
    double? DurationMilliseconds,
    long Repeats
) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            ModuleId,
            PilotId,
            ShipId,
            TargetId,
            null,
            Array.Empty<object?>(),
            (int)Effect,
            StartTime,
            DurationMilliseconds is double duration ? (object)duration : InventoryDefinitions.InfiniteEffectDuration,
            Repeats,
        };
}

internal sealed record DogmaItemInfo<T>(
    DogmaItemKey Key,
    InventoryRow? Item,
    IReadOnlyDictionary<DogmaAttributeId, T> Attributes,
    IReadOnlyDictionary<DogmaEffect, DogmaActiveEffect> ActiveEffects,
    long Time
) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["itemID"] = Key.ToBlue(),
                ["invItem"] = Item,
                ["attributes"] = new BlueMap<DogmaAttributeId, T>(Attributes),
                ["activeEffects"] = new BlueMap<DogmaEffect, DogmaActiveEffect>(ActiveEffects),
                ["time"] = Time,
                ["wallclockTime"] = Time,
            }
        );
}

internal sealed record DogmaInstanceState(
    long Id,
    bool Online = false,
    double? HullDamage = null,
    double? Capacitor = null,
    double? ArmorDamage = null,
    double? Shield = null
) : BlueModel
{
    // Uninitialized fields in pilot/module instances are integer zero, not floating zero.
    public override object ToBlue() =>
        new object?[] { Id, Online ? 1 : 0, Number(HullDamage), Number(Capacitor), 0, Number(ArmorDamage), Number(Shield), 0 };

    private static object Number(double? value) => value is double number ? number : (object)0;
}

internal sealed record MagazineQuantity(Magazine Magazine) : BlueModel
{
    public override object ToBlue() =>
        new BlueConstructor(
            "blue.DBRow",
            [
                new BlueConstructor(
                    "blue.DBRowDescriptor",
                    [
                        new object?[]
                        {
                            new object?[] { "locationID", 20 },
                            new object?[] { "flagID", 3 },
                            new object?[] { "typeID", 3 },
                            new object?[] { "quantity", 3 },
                        },
                    ]
                ),
                new object?[] { Magazine.ShipId, Magazine.FlagId, Magazine.TypeId, Magazine.Quantity },
            ]
        );
}

internal sealed record DogmaShipState(
    IReadOnlyDictionary<long, DogmaInstanceState> Instances,
    IReadOnlyDictionary<long, IReadOnlyDictionary<int, MagazineQuantity>> Quantities
) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            new BlueMap<long, DogmaInstanceState>(Instances),
            new BlueMap<long, BlueMap<int, MagazineQuantity>>(
                Quantities.ToDictionary(p => p.Key, p => new BlueMap<int, MagazineQuantity>(p.Value))
            ),
            new Dictionary<long, object?>(),
            new List<object?>(),
        };
}

internal sealed record DogmaAllInfo(
    DogmaShipState ShipState,
    IReadOnlyDictionary<long, DogmaItemInfo<double>> ShipInfo,
    IReadOnlyDictionary<DogmaItemKey.Charge, DogmaItemInfo<double>> Charges,
    DogmaItemInfo<int> Character,
    PilotBrainState Brain
) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["shipState"] = ShipState,
                ["structureInfo"] = null,
                ["shipInfo"] =
                    Charges.Count == 0
                        ? (object)new BlueMap<long, DogmaItemInfo<double>>(ShipInfo)
                        : new BlueDictionary(
                            ShipInfo
                                .Select(p => ((object?)p.Key, (object?)p.Value))
                                .Concat(Charges.Select(p => ((object?)p.Key.ToBlue(), (object?)p.Value)))
                                .ToArray()
                        ),
                ["charInfo"] = new object?[]
                {
                    new BlueMap<long, DogmaItemInfo<int>>(
                        new Dictionary<long, DogmaItemInfo<int>> { [((DogmaItemKey.Item)Character.Key).Id] = Character }
                    ),
                    Brain,
                },
                ["shipModifiedCharAttribs"] = null,
                ["systemWideEffectsOnShip"] = null,
            }
        );
}
