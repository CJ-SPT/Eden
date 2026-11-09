using Eden.Gameplay.Dogma;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Combat.Weapons;
using Eden.World.Fittings;
using Eden.World.Mining;
using Eden.World.Repair;

namespace Eden.World.Ammunition;

internal static class AmmunitionCatalog
{
    internal static bool Supported(CatalogDbContext catalog, int typeId, int group) =>
        Eden.World.Exploration.ProbeCatalog.Probe(catalog, typeId, group)
        || TurretChargeCatalog.Supported(catalog, typeId, group)
        || MissileChargeCatalog.Supported(catalog, typeId, group)
        || ScriptCatalog.Supported(catalog, typeId, group)
        || MiningCrystalCatalog.Supported(catalog, typeId, group)
        || AncillaryRepairCatalog.PasteSupported(catalog, typeId, group)
        || group == CapacitorBoosterDefinitions.ChargeGroup
            && catalog.FittingAttributes(typeId).GetValueOrDefault(DogmaAttributeId.CapacitorBonus) > 0;
}
