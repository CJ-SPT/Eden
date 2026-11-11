using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Registration;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Repair;

internal sealed partial class PilotRemoteAssistanceStore
{
    internal static IReadOnlyList<RemoteAssistanceContact> Active(WorldAction world, long shipId) =>
        Array.AsReadOnly(
            world
                .Set<WorldRemoteAssistance>()
                .Where(e => e.TargetId == shipId)
                .OrderBy(e => e.ModuleId)
                .AsNoTracking()
                .AsEnumerable()
                .Select(e => e.Contact())
                .ToArray()
        );
}
