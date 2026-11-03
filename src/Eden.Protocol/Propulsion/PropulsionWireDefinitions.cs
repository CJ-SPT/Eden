using Eden.Gameplay.Inventory;

namespace Eden.Protocol.Propulsion;

internal static class PropulsionWireDefinitions
{
    public const string EffectName = "moduleBonusAfterburner";
    public const string EffectGraphic = "effects.Afterburner";
    public const string MicrowarpdriveEffectName = "moduleBonusMicrowarpdrive";
    public const string MicrowarpdriveGraphic = "effects.MicroWarpDrive";

    internal static DogmaEffect? Effect(string name) =>
        name switch
        {
            EffectName => DogmaEffect.Afterburner,
            MicrowarpdriveEffectName => DogmaEffect.Microwarpdrive,
            _ => null,
        };

    internal static string Graphic(DogmaEffect effect) => effect == DogmaEffect.Microwarpdrive ? MicrowarpdriveGraphic : EffectGraphic;
}
