using System.Collections.Immutable;
using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Inventory;

public sealed record ShipFittingRestrictions(ImmutableHashSet<int> Groups, ImmutableHashSet<int> Types)
{
    public bool Allows(int typeId, int groupId) => Groups.Count + Types.Count == 0 || Groups.Contains(groupId) || Types.Contains(typeId);

    public static ShipFittingRestrictions Read(IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        var groups = ImmutableHashSet.CreateBuilder<int>();
        var types = ImmutableHashSet.CreateBuilder<int>();
        bool typeRestriction = false;
        foreach (var attribute in InventoryDefinitions.ShipRestrictions)
        {
            typeRestriction |= attribute == DogmaAttributeId.CanFitShipType1;
            double value = attributes.GetValueOrDefault(attribute);
            if (!double.IsFinite(value) || value < 0 || value > int.MaxValue || value != Math.Truncate(value))
            {
                throw new InvalidDataException("Invalid ship fitting restriction.");
            }
            if (value > 0)
            {
                (typeRestriction ? types : groups).Add(checked((int)value));
            }
        }
        return new(groups.ToImmutable(), types.ToImmutable());
    }
}

public sealed record FittingModuleLimits(int? MaximumGroup, int? MaximumType, int? MaximumOnline)
{
    public static FittingModuleLimits Read(IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        new(
            Limit(attributes, DogmaAttributeId.MaxGroupFitted),
            Limit(attributes, DogmaAttributeId.MaxTypeFitted),
            Limit(attributes, DogmaAttributeId.MaxGroupOnline)
        );

    private static int? Limit(IReadOnlyDictionary<DogmaAttributeId, double> attributes, DogmaAttributeId attribute)
    {
        if (!attributes.TryGetValue(attribute, out double value))
        {
            return null;
        }
        if (!double.IsFinite(value) || value < 0 || value > int.MaxValue || value != Math.Truncate(value))
        {
            throw new InvalidDataException("Invalid fitted or online module limit.");
        }
        return checked((int)value);
    }
}

public sealed record FittedModuleConstraints(long ItemId, int TypeId, int GroupId, bool Online, FittingModuleLimits Limits);

public static class ModuleFittingConstraints
{
    public static void CheckFitted(IReadOnlyList<FittedModuleConstraints> modules)
    {
        var groups = modules.GroupBy(module => module.GroupId).ToDictionary(group => group.Key, group => group.Count());
        var types = modules.GroupBy(module => module.TypeId).ToDictionary(group => group.Key, group => group.Count());
        foreach (var module in modules)
        {
            if (
                module.Limits.MaximumGroup is int maximumGroup && groups[module.GroupId] > maximumGroup
                || module.Limits.MaximumType is int maximumType && types[module.TypeId] > maximumType
            )
            {
                throw new InvalidDataException("The hull has too many modules of this group or type fitted.");
            }
        }
    }

    public static void CheckOnline(IReadOnlyList<FittedModuleConstraints> modules)
    {
        if (OnlineExcess(modules).Any())
        {
            throw new InvalidDataException("The maximum number of online modules in this group has been reached.");
        }
    }

    // Reuse Eden's deterministic highest-item-ID tie break when an attribute
    // change lowers an online limit, like the existing CPU/grid shutdown path.
    public static long? ShutdownOnlineExcess(IReadOnlyList<FittedModuleConstraints> modules) =>
        OnlineExcess(modules)
            .SelectMany(group => group.Where(module => module.Online))
            .OrderByDescending(module => module.ItemId)
            .Select(module => (long?)module.ItemId)
            .FirstOrDefault();

    private static IEnumerable<IGrouping<int, FittedModuleConstraints>> OnlineExcess(IReadOnlyList<FittedModuleConstraints> modules) =>
        modules
            .GroupBy(module => module.GroupId)
            .Where(group => group.Min(module => module.Limits.MaximumOnline) is int limit && group.Count(module => module.Online) > limit);
}
