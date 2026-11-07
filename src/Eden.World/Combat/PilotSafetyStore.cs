using Eden.Data.World.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

[Injectable]
internal sealed class PilotSafetyStore(WorldDatabase database) : IPilotSafety
{
    public ShipSafetyLevel Read(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        return world.Pilots.Single(p => p.Id == pilotId).SafetyLevel;
    }

    public void Set(long pilotId, ShipSafetyLevel level)
    {
        if (!Enum.IsDefined(level))
        {
            throw new InvalidDataException("Choose full, partial or disabled ship safety.");
        }
        using WorldDbContext world = new(database.Path);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (
            level != ShipSafetyLevel.Full
            && world.Set<Account>().Find(pilot.AccountId)!.CloneGrade == AccountCloneGrade.Alpha
            && CrimewatchPersistence.Security(catalog, CrimewatchPersistence.System(catalog, pilot.ActiveShip), false)
                >= CrimewatchRules.HighSecurity
        )
        {
            throw new InvalidDataException("Alpha pilots use full safety in high-security space.");
        }
        pilot.SafetyLevel = level;
        world.SaveChanges();
    }
}
