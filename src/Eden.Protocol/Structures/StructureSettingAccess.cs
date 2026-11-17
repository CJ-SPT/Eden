using Eden.Gameplay.Structures;

namespace Eden.Protocol.Structures;

// Build 3662168 structures/settings.py: setting IDs are a separate namespace from service IDs.
internal enum StructureSetting
{
    None = 0,
    ReprocessingTax = 3,
    MarketTax = 4,
    CanControl = 17,
    CanDock = 19,
    RentOffice = 20,
    CloneBayTax = 23,
    IndustryTax = 24,
    BiochemicalReactionTax = 26,
    HybridReactionTax = 27,
    CompositeReactionTax = 28,
    ManufacturingTax = 29,
    CapitalManufacturingTax = 30,
    SupercapitalManufacturingTax = 31,
    ResearchTax = 32,
    InventionTax = 33,
    JumpBridgeActivation = 34,
    CynoBeacon = 35,
    AutomaticMoonMining = 36,
}

internal static class StructureSettingAccess
{
    internal static StructureService? Service(StructureSetting setting) =>
        setting switch
        {
            StructureSetting.None or StructureSetting.CanDock => StructureService.Docking,
            StructureSetting.ReprocessingTax => StructureService.Reprocessing,
            StructureSetting.MarketTax => StructureService.Market,
            StructureSetting.RentOffice => StructureService.Offices,
            StructureSetting.CloneBayTax => StructureService.JumpClone,
            StructureSetting.IndustryTax => StructureService.Industry,
            StructureSetting.BiochemicalReactionTax => StructureService.BiochemicalReactions,
            StructureSetting.HybridReactionTax => StructureService.HybridReactions,
            StructureSetting.CompositeReactionTax => StructureService.CompositeReactions,
            StructureSetting.ManufacturingTax => StructureService.Manufacturing,
            StructureSetting.CapitalManufacturingTax => StructureService.CapitalManufacturing,
            StructureSetting.SupercapitalManufacturingTax => StructureService.SupercapitalManufacturing,
            StructureSetting.ResearchTax => StructureService.Laboratory,
            StructureSetting.InventionTax => StructureService.Invention,
            StructureSetting.JumpBridgeActivation => StructureService.JumpBridge,
            StructureSetting.CynoBeacon => StructureService.CynoBeacon,
            StructureSetting.AutomaticMoonMining => StructureService.AutomaticMoonMining,
            _ => null,
        };
}
