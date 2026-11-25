using Eden.Data.World.Structures;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Structures;

[Injectable]
internal sealed class StructureReinforcementDefaultsStore(WorldDatabase database) : IStructureReinforcementDefaults
{
    public int ReadHour(long pilotId)
    {
        var live = LiveWorld.Find(database.Path);
        return live?.Economy.Inspect(world =>
            {
                var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown pilot.");
                return world.Set<SavedStructureReinforcementDefault>().Find(pilot.CorporationId)?.Hour
                    ?? StructureRules.DefaultReinforceHour;
            })
            ?? StructureRules.DefaultReinforceHour;
    }

    public void SetHour(long pilotId, int hour)
    {
        if (hour is < 0 or > 23)
        {
            throw new InvalidDataException("Choose a valid reinforcement UTC hour.");
        }
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Structure defaults require the resident world.");
        using var world = live.Economy.Begin(TransactionParticipants.None);
        var corporation = CorporationRoleStore.Current(world, pilotId);
        CorporationRoleStore.RequireDirector(world, pilotId, corporation);
        var setting = world.Set<SavedStructureReinforcementDefault>().Find(corporation.Id);
        if (setting?.Hour == hour)
        {
            return;
        }
        setting ??= world.Add(new SavedStructureReinforcementDefault { CorporationId = corporation.Id }).Entity;
        setting.Hour = hour;
        world.Commit();
    }
}
