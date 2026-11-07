using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Inventory;

public sealed record HullFittingCapacity(int High, int Medium, int Low, int Rigs, int Turrets, int Launchers)
{
    public static HullFittingCapacity Read(IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        new(
            Count(attributes, DogmaAttributeId.HighSlots, InventoryDefinitions.SlotsPerRack),
            Count(attributes, DogmaAttributeId.MediumSlots, InventoryDefinitions.SlotsPerRack),
            Count(attributes, DogmaAttributeId.LowSlots, InventoryDefinitions.SlotsPerRack),
            Count(attributes, DogmaAttributeId.RigSlotsLeft, RigRules.MaximumSlots),
            Count(attributes, DogmaAttributeId.TurretSlotsLeft, InventoryDefinitions.SlotsPerRack),
            Count(attributes, DogmaAttributeId.LauncherSlotsLeft, InventoryDefinitions.SlotsPerRack)
        );

    public int Slots(ModuleRack rack) =>
        rack switch
        {
            ModuleRack.High => High,
            ModuleRack.Medium => Medium,
            ModuleRack.Low => Low,
            ModuleRack.Rig => Rigs,
            _ => throw new InvalidDataException("Unknown fitting rack."),
        };

    private static int Count(IReadOnlyDictionary<DogmaAttributeId, double> attributes, DogmaAttributeId attribute, int maximum)
    {
        double value = attributes.GetValueOrDefault(attribute);
        if (!double.IsFinite(value) || value < 0 || value > maximum || value != Math.Truncate(value))
        {
            throw new InvalidDataException("Invalid hull fitting slot or hardpoint capacity.");
        }
        return checked((int)value);
    }
}

public static class FittingSlotRules
{
    public static void Check(HullFittingCapacity capacity, IReadOnlyList<FittedResourceUsage> modules)
    {
        if (modules.Select(module => module.FlagId).Distinct().Count() != modules.Count)
        {
            throw new InvalidDataException("More than one module occupies a fitting slot.");
        }
        foreach (var module in modules)
        {
            bool fits =
                InRack(module.FlagId, InventoryFlag.HighSlot0, capacity.High)
                || InRack(module.FlagId, InventoryFlag.MediumSlot0, capacity.Medium)
                || InRack(module.FlagId, InventoryFlag.LowSlot0, capacity.Low)
                || InRack(module.FlagId, InventoryFlag.RigSlot0, capacity.Rigs);
            if (!fits)
            {
                throw new InvalidDataException("A fitted module is outside the hull's effective slot layout.");
            }
        }
        if (modules.Count(module => module.Turret) > capacity.Turrets || modules.Count(module => module.Launcher) > capacity.Launchers)
        {
            throw new InvalidDataException("The hull has too many fitted turrets or launchers.");
        }
    }

    private static bool InRack(int flag, InventoryFlag first, int count) => flag >= (int)first && flag < (int)first + count;
}
