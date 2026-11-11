using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Registration;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat.Npcs;
using Eden.World.Combat.Weapons;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime.Combat;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

internal sealed partial class PilotCombatStore
{
    internal static IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        InventoryItem module,
        FittingCalculation? calculation = null
    )
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
        var combatSkills = Enum.GetValues<CombatSkill>();
        var skillLevels = PilotSkillReader.Levels(world, catalog, pilotId, combatSkills.Select(s => (int)s).ToArray());
        var levels = combatSkills.ToDictionary(skill => skill, skill => skillLevels[(int)skill]);
        var loaded = PilotAmmunitionStore.Contents(world, module.Id);
        var cooldown = world.Set<ModuleCooldown>().Find(module.Id);
        IReadOnlyDictionary<DogmaAttributeId, double> hull = HullCatalog.UsesCatalogBonuses(catalog, pilot.ActiveShip.TypeId)
            ? new Dictionary<DogmaAttributeId, double>()
            : PilotFittingStore.ReadAttributes(catalog, pilot.ActiveShip.TypeId);
        return WeaponRules.ModuleAttributes(
            new(
                module.TypeId,
                module.GroupId,
                PilotFittingStore.ReadModuleAttributes(world, catalog, pilotId, module, calculation),
                levels,
                hull.GetValueOrDefault(DogmaAttributeId.RookieHybridOptimalBonus),
                loaded is null ? null : PilotFittingStore.ReadAttributes(catalog, loaded.TypeId),
                cooldown?.Kind == ModuleCooldownKind.Reload ? cooldown.ReadyAt : 0,
                hull.GetValueOrDefault(DogmaAttributeId.RookieProjectileDamageBonus),
                hull.GetValueOrDefault(DogmaAttributeId.RookieEnergyDamageBonus),
                hull.GetValueOrDefault(DogmaAttributeId.RookieEnergyCapacitorBonus)
            )
        );
    }
}
