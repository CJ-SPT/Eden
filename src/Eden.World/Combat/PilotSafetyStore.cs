using Eden.Data.World.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Combat;

[Injectable]
internal sealed class PilotSafetyStore(WorldDatabase database) : IPilotSafety
{
    public ShipSafetyLevel Read(long pilotId) =>
        WorldActionScope.Inspect(
            database,
            world => world.Pilots.Find(pilotId)?.SafetyLevel ?? throw new InvalidDataException("Unknown ship safety pilot.")
        );

    public void Set(long pilotId, ShipSafetyLevel level)
    {
        if (!Enum.IsDefined(level))
        {
            throw new InvalidDataException("Choose full, partial or disabled ship safety.");
        }
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown ship safety pilot.");
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
        transaction.Commit();
    }
}
