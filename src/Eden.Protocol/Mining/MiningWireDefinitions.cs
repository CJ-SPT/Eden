namespace Eden.Protocol.Mining;

internal static class MiningWireDefinitions
{
    public const string EffectName = "miningLaser";
    public const string GasEffectName = "miningClouds";

    internal static Eden.Gameplay.Inventory.DogmaEffect? Effect(string name) =>
        name switch
        {
            EffectName => Eden.Gameplay.Inventory.DogmaEffect.MiningLaser,
            GasEffectName => Eden.Gameplay.Inventory.DogmaEffect.MiningClouds,
            _ => null,
        };

    internal static string Graphic(Eden.Gameplay.Mining.MiningFamily family) =>
        family == Eden.Gameplay.Mining.MiningFamily.Gas ? "effects.CloudMining" : "effects.Laser";
}
