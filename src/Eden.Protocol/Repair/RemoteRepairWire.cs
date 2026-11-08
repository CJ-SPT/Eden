using Eden.Gameplay.Inventory;

namespace Eden.Protocol.Repair;

internal static class RemoteRepairWire
{
    internal static DogmaEffect? Effect(string? name) =>
        name switch
        {
            "shipModuleRemoteShieldBooster" => DogmaEffect.RemoteShieldBoost,
            "shipModuleRemoteArmorRepairer" => DogmaEffect.RemoteArmorRepair,
            "shipModuleRemoteHullRepairer" => DogmaEffect.RemoteHullRepair,
            "shipModuleAncillaryRemoteShieldBooster" => DogmaEffect.AncillaryRemoteShieldBoost,
            "shipModuleAncillaryRemoteArmorRepairer" => DogmaEffect.AncillaryRemoteArmorRepair,
            _ => null,
        };

    internal static string Graphic(DogmaEffect effect) =>
        effect switch
        {
            DogmaEffect.RemoteShieldBoost or DogmaEffect.AncillaryRemoteShieldBoost => "effects.ShieldTransfer",
            DogmaEffect.RemoteArmorRepair or DogmaEffect.AncillaryRemoteArmorRepair => "effects.RemoteArmourRepair",
            DogmaEffect.RemoteHullRepair => "effects.RemoteHullRepair",
            _ => throw new InvalidDataException("Unsupported remote repair graphic."),
        };
}
