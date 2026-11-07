using Eden.Gameplay.Inventory;

namespace Eden.Protocol.Repair;

internal static class ShieldBoostWireDefinitions
{
    public const string EffectName = "shieldBoosting";
    public const string EffectGraphic = "effects.ShieldBoosting";
    public const string AncillaryEffectName = "fueledShieldBoosting";

    internal static bool? Ancillary(string? name) =>
        name switch
        {
            EffectName => false,
            AncillaryEffectName => true,
            _ => null,
        };

    internal static DogmaEffect Effect(bool ancillary) => ancillary ? DogmaEffect.AncillaryShieldBoosting : DogmaEffect.ShieldBoosting;
}
