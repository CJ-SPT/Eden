using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Propulsion;

internal sealed partial class PilotPropulsionStore
{
    internal static double NavigationSpeed(WorldAction world, CatalogDbContext catalog, long pilotId, double speed) =>
        PropulsionRules.NavigationSpeed(speed, Bonus(world, catalog, pilotId, NavigationSkill.Navigation, DogmaAttributeId.VelocityBonus));

    private static double Bonus(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        NavigationSkill skill,
        DogmaAttributeId source
    ) => PilotSkillReader.Level(world, catalog, pilotId, (int)skill) * (catalog.FittingAttributes((int)skill).GetValueOrDefault(source, 0));

    internal static IReadOnlyList<NavigationBonus> NavigationBonuses(WorldAction world, CatalogDbContext catalog, long pilotId)
    {
        return new NavigationBonus[]
        {
            new(
                (int)NavigationSkill.Navigation,
                DogmaAttributeId.MaxVelocity,
                Bonus(world, catalog, pilotId, NavigationSkill.Navigation, DogmaAttributeId.VelocityBonus)
            ),
            new(
                (int)NavigationSkill.Afterburner,
                DogmaAttributeId.Duration,
                Bonus(world, catalog, pilotId, NavigationSkill.Afterburner, DogmaAttributeId.DurationBonus),
                (int)NavigationSkill.Afterburner
            ),
            new(
                (int)NavigationSkill.Afterburner,
                DogmaAttributeId.CapacitorNeed,
                Bonus(world, catalog, pilotId, NavigationSkill.Afterburner, DogmaAttributeId.CapacitorNeedBonus),
                (int)NavigationSkill.Afterburner
            ),
            new(
                (int)NavigationSkill.FuelConservation,
                DogmaAttributeId.CapacitorNeed,
                Bonus(world, catalog, pilotId, NavigationSkill.FuelConservation, DogmaAttributeId.CapacitorNeedBonus),
                (int)NavigationSkill.Afterburner
            ),
            new(
                (int)NavigationSkill.AccelerationControl,
                DogmaAttributeId.SpeedFactor,
                Bonus(world, catalog, pilotId, NavigationSkill.AccelerationControl, DogmaAttributeId.SpeedFactorBonus),
                Group: PropulsionDefinitions.AfterburnerGroup
            ),
            new(
                (int)NavigationSkill.HighSpeedManeuvering,
                DogmaAttributeId.CapacitorNeed,
                Bonus(world, catalog, pilotId, NavigationSkill.HighSpeedManeuvering, DogmaAttributeId.CapacitorNeedBonus),
                (int)NavigationSkill.HighSpeedManeuvering
            ),
        }
            .Where(b => b.Percent != 0)
            .ToArray();
    }

    internal static IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        InventoryItem module,
        FittingCalculation? calculation = null
    )
    {
        return PropulsionRules.ModuleAttributes(
            module.GroupId,
            PilotFittingStore.ReadModuleAttributes(world, catalog, pilotId, module, calculation),
            NavigationBonuses(world, catalog, pilotId)
        );
    }
}
