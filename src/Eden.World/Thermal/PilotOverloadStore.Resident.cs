using System.Collections.Frozen;
using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Thermal;

internal sealed partial class PilotOverloadStore
{
    internal static bool Selected(WorldAction world, string path, long pilot, long module)
    {
        var heat = LiveWorld.Find(path)?.HeatForKnownPilot(pilot);
        if (heat?.Selected(pilot, module) != true)
        {
            return false;
        }
        var item = world.Set<OwnedItem>().Find(module);
        return item?.OwnerId == pilot && heat.Selected(pilot, module, item.LocationId);
    }
}
