using System.Text.Json;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private static ReplacementKit? LostKit(CatalogDbContext catalog, ShipLoss loss)
    {
        OwnedItem[] items;
        try
        {
            items = JsonSerializer.Deserialize<OwnedItem[]>(loss.ItemsJson) ?? [];
        }
        catch (JsonException)
        {
            return null;
        }
        var mounted = items
            .Where(item => item.LocationId == loss.ShipId && InventoryDefinitions.IsFittedSlot(item.FlagId))
            .OrderBy(item => item.FlagId)
            .ToArray();
        var loaded = items.Where(item => item.Quantity > 0).ToLookup(item => item.LocationId);
        var ammoByModuleType = mounted
            .SelectMany(module => loaded[module.Id].Select(item => new { module.TypeId, Charge = item.TypeId }))
            .ToLookup(item => item.TypeId, item => item.Charge);
        var magazines = mounted
            .Where(module => module.Online)
            .Select(module =>
                Magazine(
                    catalog,
                    module.FlagId,
                    module.TypeId,
                    loaded[module.Id].FirstOrDefault()?.TypeId ?? ammoByModuleType[module.TypeId].FirstOrDefault()
                )
            )
            .OfType<ReplacementMagazine>()
            .ToArray();
        var drones = items
            .Where(item =>
                item.LocationId == loss.ShipId
                && item.FlagId == (int)InventoryFlag.DroneBay
                && item.Quantity > 0
                && catalog.FittingType(item.TypeId)?.CategoryId == (int)InventoryCategory.Drone
            )
            .Select(item => new SavedFittingItem(item.TypeId, InventoryFlag.DroneBay, item.Quantity))
            .ToArray();
        return new(
            loss.TypeId,
            mounted.Select(module => new FittingSlot(module.FlagId, module.TypeId)).ToArray(),
            mounted.Where(module => module.Online).Select(module => module.FlagId).ToHashSet(),
            drones,
            magazines
        );
    }

    private static IEnumerable<ReplacementKit> ReadyKits(
        CatalogDbContext catalog,
        ShipLoss loss,
        CapsuleerCareer career,
        ReplacementKit? original
    )
    {
        if (original is not null)
        {
            yield return original;
            var essential = original.Slots.Where(slot => InventoryDefinitions.IsHighSlot(slot.FlagId)).ToArray();
            if (
                essential.Length < original.Slots.Length
                && (essential.Length > 0 || original.Supplies.Length > 0)
                && WithinBaseFittingResources(catalog, original.HullTypeId, essential)
            )
            {
                var retained = essential.Select(slot => slot.FlagId).ToHashSet();
                yield return original with
                {
                    Slots = essential,
                    OnlineSlots = original.OnlineSlots.Where(retained.Contains).ToHashSet(),
                    Magazines = original.Magazines.Where(magazine => retained.Contains(magazine.Slot)).ToArray(),
                };
            }
        }

        int high = (int)InventoryFlag.HighSlot0;
        if (career == CapsuleerCareer.Mining)
        {
            yield return new((int)CapsuleerMiningHull.Venture, [new(high, (int)MiningLaserType.MinerI)], new HashSet<int> { high }, [], []);
            yield break;
        }
        var group = (HullClass?)catalog.FittingType(loss.TypeId)?.GroupId;
        if (group is HullClass.Hauler or HullClass.DeepSpaceTransport or HullClass.BlockadeRunner or HullClass.Freighter)
        {
            yield return new(HaulingRules.BadgerType, [], new HashSet<int>(), [], []);
            yield break;
        }
        if (CapsuleerPatrolFit.Starter(catalog) is { } starter)
        {
            var slots = starter
                .Equipment.Where(item =>
                    InventoryDefinitions.IsHighSlot(item.Flag)
                    && catalog.FittingType(item.TypeId)?.CategoryId == (int)InventoryCategory.Module
                )
                .Select(item => new FittingSlot(item.Flag, item.TypeId))
                .ToArray();
            var magazines = slots
                .Select(slot =>
                    Magazine(
                        catalog,
                        slot.FlagId,
                        slot.TypeId,
                        starter
                            .Equipment.FirstOrDefault(item =>
                                item.Flag == slot.FlagId && catalog.FittingType(item.TypeId)?.CategoryId == (int)InventoryCategory.Charge
                            )
                            ?.TypeId
                            ?? 0
                    )
                )
                .OfType<ReplacementMagazine>()
                .ToArray();
            yield return new(starter.HullTypeId, slots, slots.Select(slot => slot.FlagId).ToHashSet(), [], magazines);
        }
    }

    private static bool WithinBaseFittingResources(CatalogDbContext catalog, int hull, FittingSlot[] slots)
    {
        var resources = catalog.FittingAttributes(hull);
        var modules = slots.Select(slot => catalog.FittingAttributes(slot.TypeId)).ToArray();
        // An imported fit can rely on grid or CPU bonuses from the slots the fallback drops.
        return modules.Sum(module => module.GetValueOrDefault(DogmaAttributeId.Cpu))
                <= resources.GetValueOrDefault(DogmaAttributeId.CpuOutput)
            && modules.Sum(module => module.GetValueOrDefault(DogmaAttributeId.Power))
                <= resources.GetValueOrDefault(DogmaAttributeId.PowerOutput);
    }

    private static ReplacementMagazine? Magazine(CatalogDbContext catalog, int slot, int module, int charge)
    {
        if (catalog.FittingType(charge) is not { } type || !AmmunitionCatalog.Supported(catalog, charge, type.GroupId))
        {
            return null;
        }
        double volume = PilotInventoryStore.Volume(catalog, charge);
        double capacity = catalog.FittingType(module)?.Capacity ?? 0;
        int quantity = volume > 0 ? (int)Math.Floor(capacity / volume + InventoryDefinitions.ResourceTolerance) : 0;
        if (AmmunitionDefinitions.IsCrystalGroup(type.GroupId) || SupportModuleDefinitions.IsScript(type.GroupId))
        {
            quantity = Math.Min(quantity, 1);
        }
        return quantity > 0 ? new(slot, charge, quantity) : null;
    }

    private static bool SupportedKit(CatalogDbContext catalog, ReplacementKit kit, IReadOnlyList<ReplacementPart> parts) =>
        catalog.FittingType(kit.HullTypeId) is { } hull
        && HullCatalog.CanAssemble(catalog, hull, hull.CategoryId)
        && kit.Slots.Select(slot => slot.FlagId).Distinct().Count() == kit.Slots.Length
        && parts.All(part =>
            catalog.Read(CatalogData.Client.Types, (long)part.TypeId) is { Published: 1, MarketGroupID: > 0 }
            && catalog.FittingType(part.TypeId) is not null
        );
}
