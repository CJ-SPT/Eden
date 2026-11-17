using Eden.Gameplay.Corporations;
using Eden.Gameplay.Structures;
using Eden.World.Corporations;
using Eden.World.Storage;

namespace Eden.World.Structures;

internal sealed partial class PilotStructureStore
{
    public bool CanControl(long pilotId, long structureId) =>
        Execute(
            (world, catalog, _) =>
            {
                var structure = world.Set<Eden.Data.World.Structures.SavedStructure>().Find(structureId);
                var pilot = world.Pilots.Find(pilotId);
                return structure is not null
                    && pilot is not null
                    && pilot.CorporationId == structure.CorporationId
                    && world.Set<Eden.Data.World.Corporations.SavedCorporation>().Find(structure.CorporationId) is { } corporation
                    && CorporationRoleStore.Effective(world, pilotId, corporation).Roles.Has(CorporationRole.StationManager)
                    && StructureRules.CanDock(StructureCatalog.Require(catalog, structure.TypeId), structure.State, 0);
            },
            structureId: structureId
        );

    public StructureSnapshot AuthorizeControl(long pilotId, long structureId) =>
        Execute(
            (world, catalog, _) =>
            {
                var structure = Require(world, structureId);
                var pilot = Manage(world, structure, pilotId);
                if (
                    world.Ships.Find(pilot.ActiveShipId)?.StructureId != structureId
                    || world.Positions.Find(pilot.ActiveShipId) is not null
                    || !StructureRules.CanDock(StructureCatalog.Require(catalog, structure.TypeId), structure.State, 0)
                )
                {
                    throw new InvalidDataException("Dock in this operational structure before taking control.");
                }
                return Snapshot(world, catalog, structure, Now);
            },
            pilotId: pilotId,
            structureId: structureId
        );
}
