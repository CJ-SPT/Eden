using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Structures;

public static class StructureIndustryRules
{
    // Build 3662168 service module 35877, its ship restriction and sovereignty upgrade attribute 5688.
    public const int Sotiyo = 35827;
    public const int SupercapitalShipyard = 35877;
    public const int SupercapitalConstructionUpgrade = 81623;

    public static StructureService Required(IndustryActivity activity, int category = 0, int group = 0) =>
        activity switch
        {
            IndustryActivity.ResearchTime => StructureService.TimeResearch,
            IndustryActivity.ResearchMaterial => StructureService.MaterialResearch,
            IndustryActivity.Copying => StructureService.Copying,
            IndustryActivity.Invention => StructureService.Invention,
            IndustryActivity.Reaction => StructureService.CompositeReactions,
            IndustryActivity.Manufacturing when category == (int)InventoryCategory.Ship => (HullClass)group switch
            {
                HullClass.Titan or HullClass.Supercarrier => StructureService.SupercapitalManufacturing,
                HullClass.Carrier
                or HullClass.Dreadnought
                or HullClass.LancerDreadnought
                or HullClass.ForceAuxiliary
                or HullClass.CapitalIndustrial
                or HullClass.CommandCarrier => StructureService.CapitalManufacturing,
                _ => StructureService.BasicManufacturing,
            },
            IndustryActivity.Manufacturing => StructureService.BasicManufacturing,
            _ => throw new InvalidDataException("Unsupported structure industry activity."),
        };
}
