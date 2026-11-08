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
            "ShipModuleRemoteArmorMutadaptiveRepairer" => DogmaEffect.MutadaptiveRemoteArmorRepair,
            _ => null,
        };

    internal static string Graphic(DogmaEffect effect) =>
        effect switch
        {
            DogmaEffect.RemoteShieldBoost or DogmaEffect.AncillaryRemoteShieldBoost => "effects.ShieldTransfer",
            DogmaEffect.RemoteArmorRepair or DogmaEffect.AncillaryRemoteArmorRepair => "effects.RemoteArmourRepair",
            DogmaEffect.RemoteHullRepair => "effects.RemoteHullRepair",
            DogmaEffect.MutadaptiveRemoteArmorRepair => "effects.TriglavianBeam",
            _ => throw new InvalidDataException("Unsupported remote repair graphic."),
        };

    internal static IReadOnlyList<string> Graphics(DogmaEffect effect) =>
        effect == DogmaEffect.MutadaptiveRemoteArmorRepair ? [Graphic(effect), "effects.AttackMode"] : [Graphic(effect)];
}
