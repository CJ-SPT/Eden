using Eden.Data.World.Corporations;
using Eden.Data.World.Structures;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Structures;

internal sealed partial class PilotStructureStore
{
    public bool CanControl(long pilotId, long structureId) =>
        Execute(
            (world, catalog, _) =>
            {
                var structure = world.Set<SavedStructure>().Find(structureId);
                return structure is not null && CanControl(world, catalog, structure, pilotId);
            },
            structureId: structureId
        );

    internal static bool CanControl(WorldAction world, CatalogDbContext catalog, SavedStructure structure, long pilotId) =>
        world.Pilots.Find(pilotId)?.CorporationId == structure.CorporationId
        && world.Set<SavedCorporation>().Find(structure.CorporationId) is { } corporation
        && CorporationRoleStore.Effective(world, pilotId, corporation).Roles.Has(CorporationRole.StationManager)
        && StructureRules.CanDock(StructureCatalog.Require(catalog, structure.TypeId), structure.State, 0);

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
