using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Structures;

public enum StructureChargeTarget
{
    Charge,
    Module,
    Structure,
}

public sealed record StructureChargeModifier(
    StructureChargeTarget Target,
    DogmaAttributeId Attribute,
    DogmaAttributeId SourceAttribute,
    DogmaOperation Operation,
    PassiveEffectPhase Phase
);

public sealed record StructureChargeProfile(
    int TypeId,
    int GroupId,
    IReadOnlyDictionary<DogmaAttributeId, double> Attributes,
    IReadOnlyList<StructureChargeModifier> Modifiers,
    IReadOnlySet<int> RequiredSkills
);

public readonly record struct StructureLoadedCharge(long ItemId, long ModuleId, StructureChargeProfile Profile);

public sealed record StructureAmmoDestination(long LocationId, long OwnerId, int FlagId);

public sealed record StructureAmmoDeposit(long ItemId, int? Quantity = null);

public interface IPilotStructureAmmunition
{
    bool Handles(long structureId);
    StructureResult Deposit(long pilotId, long structureId, long itemId, int? quantity = null);
    StructureResult Deposit(long pilotId, long structureId, IReadOnlyList<StructureAmmoDeposit> items);
    StructureResult Withdraw(long pilotId, long structureId, long itemId, int quantity, bool toCargo);
    IReadOnlyList<Magazine> Read(long pilotId, long structureId);
    IReadOnlyDictionary<DogmaAttributeId, double> ChargeAttributes(long pilotId, long structureId, long moduleId);
    AmmunitionTransfer Load(long pilotId, long structureId, IReadOnlyList<long> moduleIds, IReadOnlyList<long> chargeIds, long sourceId);
    AmmunitionTransfer Unload(
        long pilotId,
        long structureId,
        IReadOnlyList<long> moduleIds,
        StructureAmmoDestination destination,
        int? quantity
    );
}

public static class StructureAmmunitionRules
{
    public static bool Compatible(StructureEquipmentProfile module, StructureChargeProfile charge) =>
        !module.Service
        && module.Fitting.Rack != ModuleRack.Rig
        && MissileDefinitions.ChargeGroupAttributes.Any(attribute => module.Attributes.GetValueOrDefault(attribute) == charge.GroupId)
        && (
            module.Attributes.GetValueOrDefault(DogmaAttributeId.ChargeSize) == 0
            || module.Attributes.GetValueOrDefault(DogmaAttributeId.ChargeSize)
                == charge.Attributes.GetValueOrDefault(DogmaAttributeId.ChargeSize)
        );

    public static int MaximumRounds(double capacity, double volume)
    {
        double count = Math.Floor(capacity / volume + InventoryDefinitions.ResourceTolerance);
        if (
            !double.IsFinite(capacity)
            || capacity <= 0
            || !double.IsFinite(volume)
            || volume <= 0
            || !double.IsFinite(count)
            || count < 1
            || count > int.MaxValue
        )
        {
            throw new InvalidDataException("The structure module has no valid magazine capacity for this charge.");
        }
        return checked((int)count);
    }

    public static long ReloadDeadline(double milliseconds, long now)
    {
        double ticks = Math.Ceiling(milliseconds * TimeSpan.TicksPerMillisecond);
        if (!double.IsFinite(milliseconds) || milliseconds < 0 || !double.IsFinite(ticks) || ticks >= long.MaxValue - (double)now)
        {
            throw new InvalidDataException("The structure module has an invalid reload duration.");
        }
        return checked(now + (long)ticks);
    }
}
