using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.FactionWarfare;

internal sealed partial class MilitiaComplexStore
{
    internal static bool IsSameActiveCaptureGrid(
        WorldAction world,
        CatalogDbContext catalog,
        int system,
        SpacePosition attacker,
        SpacePosition victim,
        long now
    ) =>
        FactionWarzoneCatalog.Systems.ContainsKey(system)
        && Read(world, catalog.Definitions, system, now)
            .Any(site =>
                // Use the room rather than the capture radius or the outer acceleration-gate grid.
                site.Room.DistanceTo(attacker) <= NpcEncounterDefinitions.VisibilityRange
                && site.Room.DistanceTo(victim) <= NpcEncounterDefinitions.VisibilityRange
            );
}
