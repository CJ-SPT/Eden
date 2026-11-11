using Eden.Data.World.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Structures;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Docking;

public sealed partial class SqliteWorld
{
    private static DockedPilot StructurePilot(WorldAction world, CatalogDbContext catalog, DockedPilot pilot, long structureId)
    {
        var structure = PilotStructureStore.Require(world, structureId);
        var profile = StructureCatalog.Require(catalog, structure.TypeId);
        var system = catalog.RequireSystem(structure.SystemId);
        return pilot with
        {
            Home = new(
                structure.Id,
                structure.TypeId,
                structure.CorporationId,
                new(system.Id, system.ConstellationId, system.RegionId),
                0,
                structure.MoonId ?? 0,
                new(structure.X, structure.Y, structure.Z),
                new(structure.Name, profile.GroupId, StructureRules.CategoryId)
            ),
            Flight = pilot.Flight with { StationRadius = (float)profile.Radius },
        };
    }
}
