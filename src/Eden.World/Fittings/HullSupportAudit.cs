using System.Text.Json;
using System.Text.Json.Serialization;
using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Fittings;

internal sealed record HullFittingLimits(int High, int Medium, int Low, int Turrets, int Launchers);

internal sealed record HullHoldSupport(
    InventoryFlag Flag,
    DogmaAttributeId CapacityAttribute,
    double BaseCapacity,
    ShipHoldCapacityUnit Unit
);

internal sealed record HullSupportEntry(
    int TypeId,
    string Name,
    HullClass Class,
    string Acquisition,
    HullFittingLimits Fitting,
    IReadOnlyList<int> RequiredSkills,
    double CargoCapacity,
    double GeneralMiningHoldCapacity,
    IReadOnlyList<HullHoldSupport> Holds,
    IReadOnlyList<HullEffectCapability> Effects,
    IReadOnlyList<HullCapability> Deferred
);

internal sealed record HullSupportReport(int Build, int CatalogHullCount, IReadOnlyList<HullSupportEntry> Hulls);

internal static class HullSupportAudit
{
    internal static HullSupportReport Read(CatalogDbContext catalog)
    {
        List<HullSupportEntry> entries = [];
        int count = 0;
        foreach (var type in catalog.Set<ItemType>().Include(t => t.Group).Where(t => t.Group.CategoryId == (int)InventoryCategory.Ship))
        {
            var source = catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(type.Id))?.Read<ClientTypes>();
            if (source is not { Published: 1, TechLevel: 1 })
            {
                continue;
            }
            count++;
            if (!HullCatalog.CanAssemble(catalog, type, type.Group.CategoryId))
            {
                continue;
            }
            var attributes = PilotFittingStore.ReadAttributes(catalog, type.Id).ToDictionary();
            attributes[DogmaAttributeId.CargoCapacity] = type.Capacity ?? 0;
            var effects = HullCatalog.Capabilities(catalog, type.Id).OrderBy(e => e.EffectId).ThenBy(e => e.Capability).ToArray();
            var deferred = effects.Where(e => !e.Operational).Select(e => e.Capability).ToHashSet();
            deferred.UnionWith(HullBonusRules.DeferredRoles(type.Id, (HullClass)type.GroupId));
            if (attributes.GetValueOrDefault(DogmaAttributeId.DroneCapacity) > 0)
            {
                deferred.Add(HullCapability.CombatDrones);
            }
            if (attributes.GetValueOrDefault(DogmaAttributeId.ShipMaintenanceBayCapacity) > 0)
            {
                deferred.Add(HullCapability.FleetRefitting);
            }
            if (attributes.GetValueOrDefault(DogmaAttributeId.FleetHangarCapacity) > 0)
            {
                deferred.Add(HullCapability.FleetAccess);
            }
            if (attributes.GetValueOrDefault(DogmaAttributeId.FrigateEscapeBayCapacity) > 0)
            {
                deferred.Add(HullCapability.FrigateEscapeRecovery);
            }
            entries.Add(
                new(
                    type.Id,
                    type.Name,
                    (HullClass)type.GroupId,
                    source.MarketGroupID is not null ? "market" : "recovery-or-developer-fixture",
                    new(
                        Limit(DogmaAttributeId.HighSlots),
                        Limit(DogmaAttributeId.MediumSlots),
                        Limit(DogmaAttributeId.LowSlots),
                        Limit(DogmaAttributeId.TurretSlotsLeft),
                        Limit(DogmaAttributeId.LauncherSlotsLeft)
                    ),
                    HullCatalog.Skills(catalog, type.Id).Order().ToArray(),
                    type.Capacity ?? 0,
                    attributes.GetValueOrDefault(DogmaAttributeId.GeneralMiningHoldCapacity),
                    ShipHoldRules
                        .Definitions.Where(h => attributes.GetValueOrDefault(h.CapacityAttribute) > 0)
                        .Select(h => new HullHoldSupport(h.Flag, h.CapacityAttribute, attributes[h.CapacityAttribute], h.CapacityUnit))
                        .ToArray(),
                    effects,
                    deferred.Order().ToArray()
                )
            );
            int Limit(DogmaAttributeId attribute) => checked((int)attributes.GetValueOrDefault(attribute));
        }
        return new(3662168, count, entries.OrderBy(e => e.TypeId).ToArray());
    }

    internal static void Export(string catalogPath, string output)
    {
        using CatalogDbContext catalog = new(catalogPath);
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(output, JsonSerializer.Serialize(Read(catalog), options) + Environment.NewLine);
    }
}
