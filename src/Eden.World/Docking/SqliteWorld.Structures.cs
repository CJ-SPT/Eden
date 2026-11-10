using Eden.Data.World.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Storage;
using Eden.World.Structures;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Docking;

public sealed partial class SqliteWorld
{
    private DockedPilot DockStructure(WorldDbContext world, CatalogDbContext catalog, long pilotId, long shipId, long structureId)
    {
        long now = _clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        PilotStructureStore.Advance(world, catalog, now);
        var structure = PilotStructureStore.Require(world, structureId);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var profile = StructureCatalog.Require(catalog, structure.TypeId);
        var type = catalog.RequireItem(pilot.ActiveShip.TypeId);
        var position = pilot.ActiveShip.Position;
        if (
            pilot.ActiveShipId != shipId
            || position is null
            || !PilotStructureStore.Access(world, structure, pilotId)
            || !StructureRules.CanDock(profile, structure.State, type.GroupId)
            || (position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId) != structure.SystemId
        )
        {
            throw new InvalidDataException("Docking access, structure state or ship size prevents docking here.");
        }
        CrimewatchPersistence.CheckTravel(world, catalog, pilot, CrimewatchTravel.Dock, now);
        if (
            new SpacePosition(position.X, position.Y, position.Z).DistanceTo(new(structure.X, structure.Y, structure.Z))
            > profile.Radius + (type.Radius ?? 0) + DockingDefinitions.MaximumRange
        )
        {
            throw new InvalidDataException("The structure is outside docking range.");
        }
        pilot.ActiveShip.StructureId = structureId;
        world.Positions.Remove(position);
        ShipHeatPersistence.CancelForTravel(world, shipId);
        world.SaveChanges();
        return FindPilot(world, pilot.AccountId, pilotId)!;
    }

    private static DockedPilot StructurePilot(WorldDbContext world, CatalogDbContext catalog, DockedPilot pilot, long structureId)
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
