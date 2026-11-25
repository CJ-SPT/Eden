using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Structures;

internal sealed partial class PilotStructureWeaponStore
{
    private StructureWeaponActivation ApplyElectronicWarfare(
        WorldAction world,
        CatalogDbContext catalog,
        StructureControlToken token,
        SavedStructure source,
        OwnedItem item,
        StructureTarget target,
        Pilot? victim,
        StructureFittingState fit,
        DogmaEffect effect,
        int repeats,
        long now
    )
    {
        if (target.Kind is not (StructureTargetKind.Npc or StructureTargetKind.Pilot))
        {
            throw new InvalidDataException("Choose a ship or NPC for the structure electronic warfare module.");
        }
        var attributes = TargetAttributes(world, catalog, target, victim, now);
        var module = PlayerEwarRules.Define(item.Id, item.TypeId, effect, fit.Equipment[item.Id]);
        if (effect == DogmaEffect.StructureEcm)
        {
            module = module with
            {
                CapacitorNeed =
                    module.CapacitorNeed
                    * StructureEnergy.CostMultiplier(world, catalog, token.PilotId, StructureWeaponRules.ElectronicSystemsSkill),
            };
        }
        double distance = SurfaceDistance(catalog, source, target);
        StructureWeaponRules.CheckElectronicRange(module, distance);
        double charge = StructureEnergy.Read(world, source.Id, fit.Attributes, now).Charge - module.CapacitorNeed;
        if (charge < 0)
        {
            throw new InvalidDataException("The structure has insufficient capacitor for electronic warfare.");
        }
        long ready = StructureWeaponRules.ReadyAt(module.DurationMilliseconds, now);
        var applied =
            effect == DogmaEffect.StructureEcm
                ? StructureWeaponRules.Jam(module, attributes, distance, ewarRolls.Next())
                : PlayerEwarRules.Attempt(module, distance, attributes, roll: 0);
        TackleTarget identity = new(target.Id, victim is null ? TackleTargetKind.Npc : TackleTargetKind.Ship);
        ElectronicWarfareContact contact = new(token.PilotId, source.Id, module, identity, applied, now, ready);
        StructureEnergy.Write(world, source.Id, charge, now);
        var row =
            world.Set<WorldPlayerEwar>().Find(item.Id, target.Id)
            ?? world.Add(new WorldPlayerEwar { ModuleId = item.Id, TargetId = target.Id }).Entity;
        row.PilotId = token.PilotId;
        // The existing row's ship foreign key follows the gunner's docked hull. The contact names the structure on grid.
        row.SourceShipId = world.Pilots.Find(token.PilotId)!.ActiveShipId;
        row.SystemId = source.SystemId;
        row.TargetKind = identity.Kind;
        row.ContactJson = JsonSerializer.Serialize(contact);
        return new(
            new(item.Id, target.Id, item.TypeId, now, ready, module.DurationMilliseconds, repeats, Effect: module.Effect),
            null,
            ElectronicWarfare: new(module, identity, charge, [contact])
        );
    }

    private void EndElectronicWarfare(StructureWeaponCycle cycle)
    {
        using WorldActionScope transaction = new(database, new(ships: [cycle.TargetId]));
        var world = transaction.World;
        if (world.Set<WorldPlayerEwar>().Find(cycle.ModuleId, cycle.TargetId) is { } row && row.Contact().StartedAt == cycle.StartedAt)
        {
            world.Remove(row);
        }
        transaction.Commit();
    }
}
