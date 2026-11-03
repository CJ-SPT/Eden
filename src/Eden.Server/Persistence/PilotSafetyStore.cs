using Eden.Gameplay.Combat;
using Eden.Protocol;

namespace Eden.Server.Persistence;

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
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        pilot.SafetyLevel = level;
        world.SaveChanges();
    }
}
