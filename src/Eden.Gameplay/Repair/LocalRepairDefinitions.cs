using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Repair;

public enum RepairLayer
{
    Armor = 0,
    Hull = 1,
}

public sealed record LocalRepairProfile(int GroupId, ModuleRack Rack, DogmaAttributeId AmountAttribute, DogmaEffect Effect);

public static class LocalRepairDefinitions
{
    private const int TechOne = 1;
    private const int TechTwo = 2;

    public static bool IsSupported(IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        attributes.GetValueOrDefault(DogmaAttributeId.TechLevel) is TechOne or TechTwo
        && !InventoryDefinitions.RequiresSpecificShip(attributes);

    public static LocalRepairProfile Profile(RepairLayer layer) =>
        layer switch
        {
            RepairLayer.Armor => new(
                ArmorRepairDefinitions.Group,
                ModuleRack.Low,
                DogmaAttributeId.ArmorRepairAmount,
                DogmaEffect.ArmorRepair
            ),
            RepairLayer.Hull => new(
                HullRepairDefinitions.Group,
                ModuleRack.Medium,
                DogmaAttributeId.HullRepairAmount,
                DogmaEffect.StructureRepair
            ),
            _ => throw new InvalidDataException("Unknown local repair layer."),
        };
}

public static class HullRepairDefinitions
{
    public const int Group = 63;
    public const int SmallTechOne = 524;
}
