using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;

namespace Eden.Protocol.Repair;

internal static class LocalRepairWireDefinitions
{
    public const string HullEffectName = "structureRepair";
    private const string HullEffectGraphic = "effects.StructureRepair";

    internal static RepairLayer? Layer(string? effect) =>
        effect switch
        {
            ArmorRepairWireDefinitions.EffectName => RepairLayer.Armor,
            HullEffectName => RepairLayer.Hull,
            _ => null,
        };

    internal static DogmaEffect Effect(RepairLayer layer) => LocalRepairDefinitions.Profile(layer).Effect;

    internal static DogmaAttributeId DamageAttribute(RepairLayer layer) =>
        layer switch
        {
            RepairLayer.Armor => DogmaAttributeId.ArmorDamage,
            RepairLayer.Hull => DogmaAttributeId.Damage,
            _ => throw new InvalidDataException("Unknown local repair layer."),
        };

    internal static string Graphic(RepairLayer layer) =>
        layer switch
        {
            RepairLayer.Armor => ArmorRepairWireDefinitions.EffectGraphic,
            RepairLayer.Hull => HullEffectGraphic,
            _ => throw new InvalidDataException("Unknown local repair layer."),
        };
}
