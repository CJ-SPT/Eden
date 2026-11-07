using Eden.Gameplay.Inventory;

namespace Eden.Protocol.Repair;

internal static class RemoteRepairWire
{
    internal static DogmaEffect? Effect(string? name) =>
        name switch
        {
            "shipModuleRemoteShieldBooster" => DogmaEffect.RemoteShieldBoost,
            "shipModuleRemoteArmorRepairer" => DogmaEffect.RemoteArmorRepair,
            _ => null,
        };

    internal static string Graphic(DogmaEffect effect) =>
        effect switch
        {
            DogmaEffect.RemoteShieldBoost => "effects.ShieldTransfer",
            DogmaEffect.RemoteArmorRepair => "effects.RemoteArmourRepair",
            _ => throw new InvalidDataException("Unsupported remote repair graphic."),
        };
}
