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
            _ => null,
        };

    internal static string Graphic(DogmaEffect effect) =>
        effect switch
        {
            DogmaEffect.RemoteShieldBoost => "effects.ShieldTransfer",
            DogmaEffect.RemoteArmorRepair => "effects.RemoteArmourRepair",
            DogmaEffect.RemoteHullRepair => "effects.RemoteHullRepair",
            _ => throw new InvalidDataException("Unsupported remote repair graphic."),
        };
}
