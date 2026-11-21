using System.Text.Json;
using Eden.Data.World.Docking;
using Eden.Data.World.Drones;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;

namespace Eden.World.Structures;

internal static class StructureDeparture
{
    internal static void Settle(
        WorldAction world,
        CatalogDbContext catalog,
        SavedStructure structure,
        long now,
        bool destroyed,
        IShipLootRoll? lootRoll = null
    )
    {
        if (world is not EconomyTransaction)
        {
            throw new InvalidOperationException("Structure departures require the resident world.");
        }
        bool eject =
            !AssetSafetyStore.SafetyEnabled(catalog, structure.SystemId)
            || destroyed && structure.LowPowerAt is long low && now - low >= StructureRules.AbandonmentDuration;
        AssetSafetyStore.RelocateDelivered(world, catalog, structure, now, eject);
        foreach (var squadron in world.Filter<WorldDrone>(row => row.ControllerShipId == structure.Id && row.SystemId != null).ToArray())
        {
            if (world.Set<OwnedItem>().Find(squadron.ItemId) is { } item)
            {
                world.Remove(item);
            }
        }
        var loot = lootRoll ?? new ShipLootRoll();
        long? industrialLoot = null;
        foreach (
            var job in world.Filter<IndustryJob>(j =>
                j.StationId == structure.Id
                && (j.Status == IndustryJobStatus.Installed || j.Status == IndustryJobStatus.Ready || j.Status == IndustryJobStatus.Paused)
            )
        )
        {
            // Reserved inputs were consumed on installation. Only physical surviving inputs/output become public loot.
            IReadOnlyDictionary<int, int> contents =
                job.Activity is IndustryActivity.Manufacturing or IndustryActivity.Reaction && job.EndTime <= now && job.SuspendedAt is null
                    ? new Dictionary<int, int> { [job.ProductTypeId] = job.OutputQuantity }
                    : JsonSerializer.Deserialize<Dictionary<int, int>>(job.MaterialsJson) ?? [];
            foreach (var material in contents.OrderBy(m => m.Key))
            {
                if (material.Value <= 0 || !loot.Survives())
                {
                    continue;
                }
                industrialLoot ??= AssetSafetyStore.CreateLoot(world, catalog, structure, structure.CorporationId, now);
                InventoryPersistence.MergeOutput(
                    world,
                    catalog,
                    structure.CorporationId,
                    industrialLoot.Value,
                    (int)InventoryFlag.Cargo,
                    material.Key,
                    material.Value
                );
            }
            if (job.BlueprintCopy && world.Set<OwnedItem>().Find(job.BlueprintId) is { } blueprint)
            {
                blueprint.BlueprintRuns = Math.Max(0, blueprint.BlueprintRuns - job.Runs);
                if (blueprint.BlueprintRuns == 0)
                {
                    world.Remove(blueprint);
                }
            }
            job.Status = IndustryJobStatus.Cancelled;
            job.CompletedAt = now;
            job.SuspendedAt = null;
        }
        var profile = StructureCatalog.Require(catalog, structure.TypeId);
        var boarded = world
            .Filter<Ship>(s => s.StructureId == structure.Id && s.StoredInId == null)
            .AsEnumerable()
            .Where(s => world.Pilots.Any(p => p.ActiveShipId == s.Id))
            .OrderBy(s => s.Id)
            .ToArray();
        foreach (var ship in boarded)
        {
            ship.StructureId = null;
            ship.Position = new FlightPosition
            {
                ShipId = ship.Id,
                SystemId = structure.SystemId,
                X = structure.X + profile.Radius + (catalog.RequireItem(ship.TypeId).Radius ?? 0) + 1,
                Y = structure.Y,
                Z = structure.Z,
            };
            world.Add(ship.Position);
        }
        var owners = world
            .Filter<OwnedItem>(i => i.LocationId == structure.Id)
            .Select(i => i.OwnerId)
            .Concat(world.Filter<Ship>(s => s.StructureId == structure.Id).Select(s => s.OwnerId))
            .Distinct()
            .Order()
            .ToArray();
        foreach (long owner in owners)
        {
            AssetSafetyStore.Collect(world, catalog, structure, owner, now, eject, allowEmpty: true);
        }
        if (destroyed)
        {
            long? container = industrialLoot;
            foreach (var item in world.Filter<OwnedItem>(i => i.LocationId == structure.Id).OrderBy(i => i.Id).ToArray())
            {
                bool builtin = item.TypeId == profile.BuiltinCoreTypeId || item.TypeId == profile.BuiltinModuleTypeId;
                bool core = item.TypeId == profile.CoreTypeId;
                if (builtin || InventoryDefinitions.IsRigSlot(item.FlagId) || !core && !loot.Survives())
                {
                    StructureLootStore.DestroyContents(world, item.Id);
                    world.Remove(item);
                    continue;
                }
                container ??= AssetSafetyStore.CreateLoot(world, catalog, structure, structure.CorporationId, now);
                item.LocationId = container.Value;
                item.FlagId = (int)InventoryFlag.Cargo;
                item.Online = false;
            }
            world.RemoveRange(world.Filter<SavedStructureService>(m => m.StructureId == structure.Id));
            world.RemoveRange(world.Filter<SavedStructureAccess>(m => m.StructureId == structure.Id));
            structure.UnanchorAt = null;
            structure.HasCore = false;
        }
        foreach (var extraction in world.Filter<SavedMoonExtraction>(e => e.StructureId == structure.Id && !e.Ended))
        {
            extraction.Ended = true;
        }
    }
}
