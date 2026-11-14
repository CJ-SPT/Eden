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
    internal static void ClearForTravel(WorldAction world, long shipId, int? destination = null)
    {
        if (world is not EconomyTransaction)
        {
            return;
        }
        foreach (var effect in EffectsForShip(world, shipId).Where(effect => effect.SystemId != destination))
        {
            RemoveMarker(world, effect.ModuleId, effect.SourceShipId);
            world.Remove(effect);
        }
    }

    private static WorldRemoteAssistance[] EffectsForShip(WorldAction world, long shipId) =>
        world
            .Filter<WorldRemoteAssistance>(effect => effect.SourceShipId == shipId)
            .AsEnumerable()
            .Concat(world.Filter<WorldRemoteAssistance>(effect => effect.TargetId == shipId))
            .DistinctBy(effect => effect.ModuleId)
            .ToArray();

    private static void RemoveMarker(WorldAction world, long moduleId, long shipId)
    {
        if (world.Set<ActiveFittedModule>().Find(moduleId) is { } active && active.ShipId == shipId)
        {
            world.Remove(active);
        }
    }

    internal static IReadOnlyList<RemoteAssistanceContact> Active(WorldAction world, long shipId) =>
        Array.AsReadOnly(
            world
                .Filter<WorldRemoteAssistance>(e => e.TargetId == shipId)
                .OrderBy(e => e.ModuleId)
                .AsNoTracking()
                .AsEnumerable()
                .Select(e => e.Contact())
                .ToArray()
        );
}
