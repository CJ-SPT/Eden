using Eden.Data.Catalog;
using Eden.Data.Corporations;
using Eden.Data.Skills;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage.Migrations;

internal static class SavedWorldValidation
{
    internal static void ReconcileAndCheck(WorldDbContext world, CatalogDbContext catalog)
    {
        world.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO WorldHomeStation(PilotId,StationId)
                SELECT p.Id,s.StationId FROM WorldPilot p JOIN WorldShip s ON s.Id=p.ActiveShipId;
            """
        );
        var skillTypes = world.Set<PilotSkill>().Select(s => s.TypeId).Distinct().ToArray();
        var rankedSkills = catalog
            .Set<ItemType>()
            .Where(t => skillTypes.Contains(t.Id) && t.Group.CategoryId == 16 && t.Attributes.Any(a => a.AttributeId == 275 && a.Value > 0))
            .Count();
        if (rankedSkills != skillTypes.Length)
        {
            throw new InvalidDataException("Save references an unknown skill or training multiplier.");
        }
        foreach (int type in world.Ships.Select(s => s.TypeId).Distinct())
        {
            if (!catalog.Set<ItemType>().Any(t => t.Id == type))
            {
                throw new InvalidDataException($"Save references unknown ship type {type}.");
            }
        }
        foreach (int station in world.Ships.Select(s => s.StationId).Distinct())
        {
            if (!catalog.Set<Station>().Any(s => s.Id == station))
            {
                throw new InvalidDataException($"Save references unknown station {station}.");
            }
        }
        CorporationSchema.Upgrade(world);
        foreach (int corporation in world.Pilots.Select(p => p.CorporationId).Distinct())
        {
            if (
                !catalog.Set<NpcCorporation>().Any(c => c.Id == corporation) && !world.Set<SavedCorporation>().Any(c => c.Id == corporation)
            )
            {
                throw new InvalidDataException($"Save references unknown pilot corporation {corporation}.");
            }
        }
        ShipStoragePersistence.Check(world);
    }
}
