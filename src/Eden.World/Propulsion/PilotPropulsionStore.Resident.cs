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
}
