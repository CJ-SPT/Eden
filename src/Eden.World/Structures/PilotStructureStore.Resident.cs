using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Structures;

internal sealed partial class PilotStructureStore
{
    internal static SavedStructure Require(WorldAction world, long id) =>
        world.Set<SavedStructure>().Find(id) is { State: not StructureState.Destroyed } structure
            ? structure
            : throw new InvalidDataException("The structure is unavailable.");

    internal static bool Access(WorldAction world, SavedStructure structure, long pilotId)
    {
        var pilot = world.Pilots.Find(pilotId);
        if (pilot is null)
        {
            return false;
        }
        var entries = world.Set<SavedStructureAccess>().Where(a => a.StructureId == structure.Id).ToArray();
        if (entries.FirstOrDefault(a => a.MemberId == pilotId) is { } individual)
        {
            return individual.Allowed;
        }
        if (entries.FirstOrDefault(a => a.MemberId == pilot.CorporationId) is { } corporation)
        {
            return corporation.Allowed;
        }
        return pilot.CorporationId == structure.CorporationId || structure.PublicAccess;
    }
}
