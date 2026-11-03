using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Repair;

public sealed record Hardener(long ItemId, int TypeId, DogmaEffect Effect, double DurationMilliseconds, double CapacitorNeed);

public sealed record HardenerActivation(Hardener Module, double Capacitor, IReadOnlyDictionary<DogmaAttributeId, double> ShipAttributes);

public interface IPilotHardening
{
    HardenerActivation Activate(long pilotId, long moduleId, DogmaEffect effect, long now);
    IReadOnlyDictionary<DogmaAttributeId, double> Stop(long pilotId, long shipId, long moduleId);
}

public static class HardenerDefinitions
{
    public const int ShieldGroup = 77;
    public const int ArmorGroup = 328;
    public const int EmShieldTechOne = 2293;
    public const int EmArmorTechOne = 11301;
    public const int TacticalShieldManipulationSkill = 3420;

    public static DogmaEffect? Effect(int group) =>
        group switch
        {
            ShieldGroup => DogmaEffect.ActiveShieldResistance,
            ArmorGroup => DogmaEffect.ActiveArmorResistance,
            _ => null,
        };

    public static Hardener Define(long itemId, int typeId, int group, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        var effect = Effect(group) ?? throw new InvalidDataException("Unsupported hardener family.");
        Hardener module = new(
            itemId,
            typeId,
            effect,
            attributes.GetValueOrDefault(DogmaAttributeId.Duration),
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed)
        );
        if (
            !double.IsFinite(module.DurationMilliseconds)
            || module.DurationMilliseconds <= 0
            || !double.IsFinite(module.CapacitorNeed)
            || module.CapacitorNeed < 0
        )
        {
            throw new InvalidDataException("Invalid hardener cycle attributes.");
        }
        return module;
    }
}
