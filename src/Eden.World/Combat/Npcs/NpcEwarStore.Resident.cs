using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Combat;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

internal sealed partial class NpcEwarStore
{
    internal static IReadOnlyList<NpcEwarContact> Active(WorldAction world, long shipId) =>
        world
            .Set<WorldNpcEwar>()
            .Where(e => e.TargetShipId == shipId && e.AppliedJson != null)
            .AsNoTracking()
            .AsEnumerable()
            .Select(e => e.Contact()!)
            .ToArray();
}
