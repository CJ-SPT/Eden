using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Structures;

[Injectable]
internal sealed class StructureCompressionStore(WorldDatabase database, TimeProvider clock) : IStructureCompression
{
    private sealed record Facility(int HullTypeId, int ModuleTypeId);

    public GasDecompressionEfficiency Efficiency(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var facility = FacilityAt(world, catalog, pilot);
        var result = facility is null ? new GasDecompressionEfficiency(0, 0) : Efficiency(world, catalog, pilotId, facility);
        world.SaveChanges();
        transaction.Commit();
        return result;
    }

    public StructureCompressionResult Compress(long pilotId, long itemId) => Convert(pilotId, itemId, false);

    public StructureCompressionResult DecompressGas(long pilotId, long itemId) => Convert(pilotId, itemId, true);

    private StructureCompressionResult Convert(long pilotId, long itemId, bool decompress)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var facility =
            FacilityAt(world, catalog, pilot)
            ?? throw new InvalidDataException("Dock at an accessible structure with its reprocessing service online.");
        var item = world.Set<OwnedItem>().Find(itemId);
        if (item is null || item.OwnerId != pilotId || item.Singleton || item.Quantity <= 0)
        {
            throw new InvalidDataException("Choose an owned resource stack.");
        }
        bool hangar = item.LocationId == pilot.ActiveShip.DockedLocationId && item.FlagId == (int)InventoryFlag.Hangar;
        bool hold = !decompress && item.LocationId == pilot.ActiveShipId && ShipHoldRules.Definition(item.FlagId) is not null;
        if (!hangar && !hold)
        {
            throw new InvalidDataException("Choose a stack in your current structure hangar or an eligible active-ship hold.");
        }
        int outputType = decompress
            ? StructureCompressionCatalog.DecompressedGas(catalog, item.TypeId)
            : StructureCompressionCatalog.Compressed(catalog, item.TypeId);
        int outputQuantity = decompress
            ? StructureCompressionRules.Decompress(item.Quantity, Efficiency(world, catalog, pilotId, facility))
            : item.Quantity;
        var before = PilotInventoryStore.Read(catalog, item);
        if (outputQuantity > 0)
        {
            item.TypeId = outputType;
            item.Quantity = outputQuantity;
        }
        var after = outputQuantity > 0 ? PilotInventoryStore.Read(catalog, item) : before with { Quantity = 0 };
        if (hold)
        {
            var definition = ShipHoldRules.Definition(item.FlagId)!;
            if (
                !definition.Allows(before)
                || !definition.Allows(after)
                || definition.RequiredCapacity(after, after.Quantity)
                    > definition.RequiredCapacity(before, before.Quantity) + InventoryDefinitions.ResourceTolerance
            )
            {
                throw new InvalidDataException("The compressed output cannot occupy this ship hold.");
            }
        }
        if (outputQuantity == 0)
        {
            world.Remove(item);
        }
        world.SaveChanges();
        transaction.Commit();
        return new(
            before.Id,
            before.TypeId,
            before.Quantity,
            outputQuantity > 0 ? before.Id : null,
            outputType,
            outputQuantity,
            new(after, before)
        );
    }

    private Facility? FacilityAt(WorldDbContext world, CatalogDbContext catalog, Pilot pilot)
    {
        if (pilot.ActiveShip.Position is not null || pilot.ActiveShip.StructureId is not long structureId)
        {
            return null;
        }
        var structure = PilotStructureStore.Require(world, structureId);
        PilotStructureStore.Advance(world, catalog, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(), structure.SystemId);
        var snapshot = PilotStructureStore.Snapshot(world, catalog, structure);
        if (!PilotStructureStore.Access(world, structure, pilot.Id) || !snapshot.Services.Contains(StructureService.Reprocessing))
        {
            return null;
        }
        var module = snapshot.Modules.Single(m =>
            m.Online && StructureCatalog.Service(catalog, m.TypeId).Services.Contains(StructureService.Reprocessing)
        );
        return new(snapshot.Profile.TypeId, module.TypeId);
    }

    private static GasDecompressionEfficiency Efficiency(WorldDbContext world, CatalogDbContext catalog, long pilotId, Facility facility)
    {
        int level = PilotSkillReader.Level(world, catalog, pilotId, StructureCompressionRules.GasDecompressionSkill);
        return StructureCompressionRules.Efficiency(
            Value(catalog, facility.ModuleTypeId, DogmaAttributeId.GasDecompressionBaseEfficiency),
            Value(catalog, facility.HullTypeId, DogmaAttributeId.GasDecompressionStructureBonus),
            level == 0
                ? 0
                : Value(catalog, StructureCompressionRules.GasDecompressionSkill, DogmaAttributeId.GasDecompressionEfficiencyBonus),
            level
        );
    }

    private static decimal Value(CatalogDbContext catalog, int typeId, DogmaAttributeId attribute)
    {
        double value = catalog.FittingAttributes(typeId).GetValueOrDefault(attribute);
        if (!double.IsFinite(value) || value is < 0 or > 100)
        {
            throw new InvalidDataException("Invalid catalog decompression attribute.");
        }
        return (decimal)value;
    }

    private static Pilot Pilot(WorldDbContext world, long pilotId) =>
        world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == pilotId)
        ?? throw new InvalidDataException("Unknown resource owner.");
}
