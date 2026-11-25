using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;

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
    public const int CapitalArmorGroup = 1699;
    public const int CapitalShieldGroup = 1700;
    public const int ArmorScriptGroup = 1701;
    public const int ShieldScriptGroup = 1702;
    public const int ResistancePhasingSkill = 32797;
    public const int EmShieldTechOne = 2293;
    public const int EmArmorTechOne = 11301;
    public const int TacticalShieldManipulationSkill = (int)Eden.Gameplay.Combat.DefenseSkill.TacticalShieldManipulation;

    public static DogmaEffect? Effect(int group) =>
        group switch
        {
            ShieldGroup or CapitalShieldGroup => DogmaEffect.ActiveShieldResistance,
            ArmorGroup or CapitalArmorGroup => DogmaEffect.ActiveArmorResistance,
            _ => null,
        };

    public static bool IsFlex(int group) => group is CapitalArmorGroup or CapitalShieldGroup;

    // The armor module description restricts it to capitals. Shield profiles also declare a more specific list.
    public static bool CanFitHull(int group, int hullGroup) =>
        !IsFlex(group)
        || CapitalTravelRules.RestrictedCapital(hullGroup)
        || hullGroup is (int)HullClass.Freighter or (int)HullClass.JumpFreighter;

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
