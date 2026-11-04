using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;

namespace Eden.Gameplay.Characters;

public sealed record LearningImplant(int TypeId, int Slot, DogmaAttributeId Attribute, int Bonus);

public sealed record InstalledImplant(long ItemId, long CloneId, LearningImplant Profile);

public sealed record ImplantSnapshot(IReadOnlyList<InstalledImplant> Implants, CharacterAttributes Attributes)
{
    public long Revision { get; init; }
}

public sealed record ImplantChange(ImplantSnapshot Snapshot, TrainingState Training, IReadOnlyList<InventoryChange> Changes);

public interface IPilotImplants
{
    ImplantSnapshot Read(long pilotId);
    ImplantChange Install(long pilotId, long itemId);
    ImplantChange Destroy(long pilotId, long itemId);
}

public static class ImplantRules
{
    // Build 3662168: Cyber Learning group and implantness slots 1..5.
    public const int LearningGroup = 745;
    public const int MaximumLearningBonus = 5;

    public static DogmaAttributeId AttributeForSlot(int slot) =>
        slot switch
        {
            1 => DogmaAttributeId.Perception,
            2 => DogmaAttributeId.Memory,
            3 => DogmaAttributeId.Willpower,
            4 => DogmaAttributeId.Intelligence,
            5 => DogmaAttributeId.Charisma,
            _ => throw new InvalidDataException("Unsupported learning implant slot."),
        };

    public static void Check(LearningImplant profile, IEnumerable<InstalledImplant> installed)
    {
        if (profile.Attribute != AttributeForSlot(profile.Slot) || profile.Bonus is < 1 or > MaximumLearningBonus)
        {
            throw new InvalidDataException("Unsupported learning implant profile.");
        }
        if (installed.Any(i => i.Profile.Slot == profile.Slot))
        {
            throw new InvalidDataException("An implant already occupies that slot. Unplugging it will destroy it.");
        }
    }

    public static void CheckSkills(
        IReadOnlyList<TrainingTarget> requirements,
        IReadOnlyDictionary<int, int> trained,
        IReadOnlyDictionary<int, int>? alphaCaps
    )
    {
        foreach (var requirement in requirements)
        {
            int level = trained.GetValueOrDefault(requirement.TypeId);
            if (alphaCaps is not null)
            {
                level = Math.Min(level, alphaCaps.GetValueOrDefault(requirement.TypeId));
            }
            if (requirement.Level is < 1 or > 5 || level < requirement.Level)
            {
                throw new InvalidDataException("Your effective skills and account status do not meet this item's requirements.");
            }
        }
    }

    public static CharacterAttributes Effective(CharacterAttributes basis, IEnumerable<InstalledImplant> implants)
    {
        var bonuses = implants.GroupBy(i => i.Profile.Attribute).ToDictionary(g => g.Key, g => g.Sum(i => i.Profile.Bonus));
        return new(
            checked(basis.Intelligence + bonuses.GetValueOrDefault(DogmaAttributeId.Intelligence)),
            checked(basis.Memory + bonuses.GetValueOrDefault(DogmaAttributeId.Memory)),
            checked(basis.Charisma + bonuses.GetValueOrDefault(DogmaAttributeId.Charisma)),
            checked(basis.Perception + bonuses.GetValueOrDefault(DogmaAttributeId.Perception)),
            checked(basis.Willpower + bonuses.GetValueOrDefault(DogmaAttributeId.Willpower))
        );
    }
}
