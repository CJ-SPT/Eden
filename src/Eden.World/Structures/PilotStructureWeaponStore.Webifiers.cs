using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Data.World.Structures;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Structures;

internal sealed partial class PilotStructureWeaponStore
{
    private StructureWeaponActivation Web(
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
            throw new InvalidDataException("Choose a ship or NPC for the structure webifier.");
        }
        _ = TargetAttributes(world, catalog, target, victim, now);
        var module = TackleDefinitions.Define(item.Id, item.TypeId, DogmaEffect.StructureStasisWebifier, fit.Equipment[item.Id]);
        double distance = SurfaceDistance(catalog, source, target);
        if (distance > module.Range && module.Falloff == 0)
        {
            throw new InvalidDataException("The target is outside webifier range.");
        }
        module = TackleDefinitions.AtDistance(module, distance);
        double cost =
            module.CapacitorNeed
            * StructureEnergy.CostMultiplier(world, catalog, token.PilotId, StructureWeaponRules.ElectronicSystemsSkill);
        module = module with { CapacitorNeed = cost };
        double charge = StructureEnergy.Read(world, source.Id, fit.Attributes, now).Charge - cost;
        if (charge < 0)
        {
            throw new InvalidDataException("The structure has insufficient capacitor for the webifier.");
        }
        long end = StructureWeaponRules.ReadyAt(module.DurationMilliseconds, now);
        long ready = StructureWeaponRules.ReadyAt(module.ReactivationMilliseconds, end);
        StructureEnergy.Write(world, source.Id, charge, now);
        var row = world.Set<WorldTackle>().Find(item.Id) ?? world.Add(new WorldTackle { ModuleId = item.Id }).Entity;
        row.PilotId = token.PilotId;
        row.SourceStructureId = source.Id;
        row.TypeId = item.TypeId;
        row.SystemId = source.SystemId;
        row.TargetId = target.Id;
        row.TargetKind = victim is null ? TackleTargetKind.Npc : TackleTargetKind.Ship;
        row.Effect = module.Effect;
        row.DurationMilliseconds = module.DurationMilliseconds;
        row.CapacitorNeed = cost;
        row.Range = module.Range;
        row.WebPercent = module.WebPercent;
        row.StartedAt = now;
        row.ExpiresAt = end;
        return new(
            new(
                item.Id,
                target.Id,
                item.TypeId,
                now,
                end,
                module.DurationMilliseconds,
                repeats,
                Effect: module.Effect,
                ReactivationAt: ready
            ),
            null,
            Web: new(module, new(target.Id, row.TargetKind), charge, ready)
        );
    }

    private void EndWeb(StructureWeaponCycle cycle)
    {
        using WorldActionScope transaction = new(database, new(ships: [cycle.TargetId]));
        if (transaction.World.Set<WorldTackle>().Find(cycle.ModuleId) is { } row && row.StartedAt == cycle.StartedAt)
        {
            transaction.World.Remove(row);
        }
        transaction.Commit();
    }
}
