using System.Collections.Immutable;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Structures;

public sealed record StructureEquipmentProfile(
    int TypeId,
    int GroupId,
    ModuleFittingDefinition Fitting,
    ShipFittingRestrictions Restrictions,
    FittingModuleLimits Limits,
    IReadOnlyDictionary<DogmaAttributeId, double> Attributes,
    IReadOnlyList<PassiveModifier> Modifiers,
    IReadOnlySet<int> RequiredSkills,
    int RigFamilyTypeId,
    bool Service = false
);

public readonly record struct StructureFittedEquipment(long ItemId, int FlagId, bool Online, StructureEquipmentProfile Profile);

public sealed record StructureFittingState(
    IReadOnlyDictionary<DogmaAttributeId, double> Attributes,
    IReadOnlyDictionary<long, IReadOnlyDictionary<DogmaAttributeId, double>> Equipment,
    StructureFittingCapacity Capacity
)
{
    public IReadOnlyDictionary<long, IReadOnlyDictionary<DogmaAttributeId, double>> Charges { get; init; } =
        ImmutableDictionary<long, IReadOnlyDictionary<DogmaAttributeId, double>>.Empty;
}

public sealed record StructureFittingCapacity(int Low, int Medium, int High, int Rigs, int Launchers, int Turrets)
{
    public int Slots(ModuleRack rack) =>
        rack switch
        {
            ModuleRack.Low => Low,
            ModuleRack.Medium => Medium,
            ModuleRack.High => High,
            _ => Rigs,
        };
}

public interface IPilotStructureFitting
{
    StructureResult Fit(long pilotId, long structureId, IReadOnlyList<StructureServiceFit> equipment);
    StructureResult SetOnline(long pilotId, long structureId, long itemId, bool online);
    StructureResult Remove(long pilotId, long structureId, long itemId);
    StructureFittingState Read(long pilotId, long structureId);
}

public static class StructureFittingRules
{
    // Pinned structure rig security coefficients. The selected multiplier feeds the rig's existing dogma effects.
    public const DogmaAttributeId RigHighSecurity = (DogmaAttributeId)2355;
    public const DogmaAttributeId RigLowSecurity = (DogmaAttributeId)2356;
    public const DogmaAttributeId RigNullSecurity = (DogmaAttributeId)2357;
    public const DogmaAttributeId RigSecurityMultiplier = (DogmaAttributeId)2358;

    public static StructureFittingState Calculate(
        long structureId,
        IReadOnlyDictionary<DogmaAttributeId, double> basis,
        IReadOnlyList<StructureFittedEquipment> equipment,
        IReadOnlyList<PassiveModifier> hullModifiers,
        IReadOnlyDictionary<DogmaAttributeId, DogmaAttributeDefinition> definitions,
        double security,
        IReadOnlyList<StructureLoadedCharge>? loaded = null
    )
    {
        List<DogmaNode> nodes = [new(structureId, basis)];
        List<DogmaModifier> modifiers = [];
        var charges = loaded ?? [];
        var locationItems = equipment
            .Select(item => new DogmaEquipment(item.ItemId, item.Profile.GroupId, item.Profile.RequiredSkills))
            .Concat(charges.Select(charge => new DogmaEquipment(charge.ItemId, charge.Profile.GroupId, charge.Profile.RequiredSkills)))
            .ToArray();
        Add(structureId, hullModifiers, true, true);
        foreach (var item in equipment)
        {
            var attributes = item.Profile.Attributes.ToDictionary();
            if (item.Profile.Fitting.Rack == ModuleRack.Rig)
            {
                var coefficient =
                    security >= StructureRules.HighSecurityBoundary ? RigHighSecurity
                    : security > 0 ? RigLowSecurity
                    : RigNullSecurity;
                attributes[RigSecurityMultiplier] = attributes.GetValueOrDefault(coefficient, 1);
            }
            nodes.Add(new(item.ItemId, attributes));
            Add(item.ItemId, item.Profile.Modifiers, item.Online, false);
        }
        foreach (var charge in charges)
        {
            nodes.Add(new(charge.ItemId, charge.Profile.Attributes));
            var module = equipment.Single(item => item.ItemId == charge.ModuleId);
            foreach (var modifier in charge.Profile.Modifiers)
            {
                if (modifier.Phase != PassiveEffectPhase.Fitted && !module.Online)
                {
                    continue;
                }
                long target = modifier.Target switch
                {
                    StructureChargeTarget.Module => charge.ModuleId,
                    StructureChargeTarget.Structure => structureId,
                    _ => charge.ItemId,
                };
                modifiers.Add(
                    new(target, modifier.Attribute, modifier.Operation, new DogmaValue.Attribute(charge.ItemId, modifier.SourceAttribute))
                );
            }
        }
        var graph = new DogmaGraph(nodes, modifiers, definitions);
        var moduleAttributes = equipment.ToImmutableDictionary(
            e => e.ItemId,
            e => (IReadOnlyDictionary<DogmaAttributeId, double>)graph.Attributes(e.ItemId).ToImmutableDictionary()
        );
        var hull = graph.Attributes(structureId).ToDictionary();
        StructureFittingCapacity capacity = new(
            checked((int)hull.GetValueOrDefault(DogmaAttributeId.LowSlots)),
            checked((int)hull.GetValueOrDefault(DogmaAttributeId.MediumSlots)),
            checked((int)hull.GetValueOrDefault(DogmaAttributeId.HighSlots)),
            checked((int)hull.GetValueOrDefault(DogmaAttributeId.RigSlotsLeft)),
            checked((int)hull.GetValueOrDefault(DogmaAttributeId.LauncherSlotsLeft)),
            checked((int)hull.GetValueOrDefault(DogmaAttributeId.TurretSlotsLeft))
        );
        hull[DogmaAttributeId.RigSlotsLeft] = Math.Max(0, capacity.Rigs - equipment.Count(e => e.Profile.Fitting.Rack == ModuleRack.Rig));
        hull[DogmaAttributeId.LauncherSlotsLeft] = Math.Max(0, capacity.Launchers - equipment.Count(e => e.Profile.Fitting.Launcher));
        hull[DogmaAttributeId.TurretSlotsLeft] = Math.Max(0, capacity.Turrets - equipment.Count(e => e.Profile.Fitting.Turret));
        hull[DogmaAttributeId.CpuLoad] = equipment
            .Where(e => e.Online)
            .Sum(e => moduleAttributes[e.ItemId].GetValueOrDefault(DogmaAttributeId.Cpu));
        hull[DogmaAttributeId.PowerLoad] = equipment
            .Where(e => e.Online)
            .Sum(e => moduleAttributes[e.ItemId].GetValueOrDefault(DogmaAttributeId.Power));
        hull[DogmaAttributeId.CalibrationLoad] = equipment.Sum(e =>
            moduleAttributes[e.ItemId].GetValueOrDefault(DogmaAttributeId.CalibrationCost)
        );
        return new(hull.ToImmutableDictionary(), moduleAttributes, capacity)
        {
            Charges = charges.ToImmutableDictionary(
                charge => charge.ModuleId,
                charge => (IReadOnlyDictionary<DogmaAttributeId, double>)graph.Attributes(charge.ItemId).ToImmutableDictionary()
            ),
        };

        void Add(long source, IReadOnlyList<PassiveModifier> entries, bool online, bool exempt)
        {
            foreach (var modifier in entries)
            {
                if (modifier.Phase != PassiveEffectPhase.Fitted && !online)
                {
                    continue;
                }
                IEnumerable<long> targets = modifier.Selector switch
                {
                    ModifierSelector.Group => locationItems.Where(e => e.GroupId == modifier.SelectionId).Select(e => e.ItemId),
                    ModifierSelector.RequiredSkill => locationItems
                        .Where(e => e.RequiredSkills.Contains(modifier.SelectionId!.Value))
                        .Select(e => e.ItemId),
                    ModifierSelector.Location => locationItems.Select(e => e.ItemId),
                    ModifierSelector.Item => [modifier.Domain == ModifierDomain.Ship ? structureId : source],
                    _ => [],
                };
                foreach (long target in targets)
                {
                    modifiers.Add(
                        new(
                            target,
                            modifier.Attribute,
                            modifier.Operation,
                            new DogmaValue.Attribute(source, modifier.SourceAttribute),
                            exempt
                        )
                    );
                }
            }
        }
    }

    private sealed record DogmaEquipment(long ItemId, int GroupId, IReadOnlySet<int> RequiredSkills);

    public static void Check(StructureProfile structure, StructureFittingState state, IReadOnlyList<StructureFittedEquipment> equipment)
    {
        if (equipment.Select(e => e.FlagId).Distinct().Count() != equipment.Count)
        {
            throw new InvalidDataException("A structure fitting slot is already occupied.");
        }
        foreach (var item in equipment)
        {
            var profile = item.Profile;
            if (profile.Service)
            {
                continue;
            }
            int slots = state.Capacity.Slots(profile.Fitting.Rack);
            if (
                !profile.Restrictions.Allows(structure.TypeId, structure.GroupId)
                || item.FlagId < profile.Fitting.FirstFlag
                || item.FlagId >= profile.Fitting.FirstFlag + slots
            )
            {
                throw new InvalidDataException("The equipment does not fit this structure or slot.");
            }
            if (
                profile.Fitting.Rack == ModuleRack.Rig
                && (
                    profile.Attributes.GetValueOrDefault(DogmaAttributeId.RigSize) != (int)structure.Size + 1
                    || equipment.Count(e =>
                        e.Profile.Fitting.Rack == ModuleRack.Rig && e.Profile.RigFamilyTypeId == profile.RigFamilyTypeId
                    ) > 1
                )
            )
            {
                throw new InvalidDataException("Use the correct rig size and at most one rig from each variation family.");
            }
        }
        var constraints = equipment
            .Select(e => new FittedModuleConstraints(e.ItemId, e.Profile.TypeId, e.Profile.GroupId, e.Online, e.Profile.Limits))
            .ToArray();
        ModuleFittingConstraints.CheckFitted(constraints);
        ModuleFittingConstraints.CheckOnline(constraints);
        if (
            equipment.Count(e => e.Profile.Fitting.Launcher) > state.Capacity.Launchers
            || equipment.Count(e => e.Profile.Fitting.Turret) > state.Capacity.Turrets
        )
        {
            throw new InvalidDataException("The structure does not have enough weapon hardpoints.");
        }
        if (
            state.Attributes.GetValueOrDefault(DogmaAttributeId.CpuLoad) > state.Attributes.GetValueOrDefault(DogmaAttributeId.CpuOutput)
            || state.Attributes.GetValueOrDefault(DogmaAttributeId.PowerLoad)
                > state.Attributes.GetValueOrDefault(DogmaAttributeId.PowerOutput)
            || state.Attributes.GetValueOrDefault(DogmaAttributeId.CalibrationLoad)
                > state.Attributes.GetValueOrDefault(DogmaAttributeId.CalibrationCapacity)
        )
        {
            throw new InvalidDataException("The structure does not have enough CPU, powergrid or calibration for this fitting.");
        }
    }
}
