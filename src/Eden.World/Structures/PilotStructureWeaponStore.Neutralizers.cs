using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;

namespace Eden.World.Structures;

internal sealed partial class PilotStructureWeaponStore
{
    private StructureWeaponActivation Neutralize(
        WorldAction world,
        CatalogDbContext catalog,
        StructureControlToken token,
        SavedStructure source,
        OwnedItem item,
        StructureTarget target,
        Pilot? victim,
        StructureFittingState fit,
        int repeats,
        long now
    )
    {
        if (target.Kind is not (StructureTargetKind.Npc or StructureTargetKind.Pilot))
        {
            throw new InvalidDataException("Choose a ship or NPC for the structure energy neutralizer.");
        }
        var module = CapacitorWarfareRules.Define(item.Id, item.TypeId, DogmaEffect.StructureEnergyNeutralizer, fit.Equipment[item.Id]);
        module = module with
        {
            CapacitorNeed =
                module.CapacitorNeed
                * StructureEnergy.CostMultiplier(world, catalog, token.PilotId, StructureWeaponRules.EngineeringSystemsSkill),
        };
        var attributes = TargetAttributes(world, catalog, target, victim, now);
        double distance = SurfaceDistance(catalog, source, target);
        if (distance > module.Optimal && module.Falloff == 0)
        {
            throw new InvalidDataException("The neutralizer target is outside range.");
        }
        var npcEnergy = victim is null ? world.Set<WorldNpcEnergy>().Find(target.Id) : null;
        var targetPool = victim is null
            ? NpcCapacitorRules.Read(NpcCapacitorRules.Profile(attributes), npcEnergy?.State(), now)
            : new CapacitorPool(
                ShipResources.Capacitor(world, target.Id, attributes, now),
                attributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity)
            );
        var applied = CapacitorWarfareRules.Apply(
            module,
            StructureEnergy.Read(world, source.Id, fit.Attributes, now),
            targetPool,
            distance,
            target.SignatureRadius,
            attributes.GetValueOrDefault(DogmaAttributeId.EnergyWarfareResistance, 1)
        );
        long ready = StructureWeaponRules.ReadyAt(module.DurationMilliseconds, now);
        StructureEnergy.Write(world, source.Id, applied.Source, now);
        if (victim is null)
        {
            npcEnergy ??= world.Add(new WorldNpcEnergy { NpcId = target.Id }).Entity;
            npcEnergy.Charge = applied.Target;
            npcEnergy.UpdatedAt = Math.Max(now, npcEnergy.UpdatedAt);
        }
        else
        {
            ShipResources.SetCapacitor(world, target.Id, applied.Target, now);
        }
        CapacitorCycle cycle = new(module, source.Id, target.Id, now, ready, victim is null ? TackleTargetKind.Npc : TackleTargetKind.Ship);
        return new(
            new(item.Id, target.Id, item.TypeId, now, ready, module.DurationMilliseconds, repeats, Effect: module.Effect),
            null,
            new(cycle, applied.Source, applied.Target, applied.Amount)
        );
    }
}
