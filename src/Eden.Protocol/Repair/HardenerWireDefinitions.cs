using Eden.Gameplay.Inventory;

namespace Eden.Protocol.Repair;

internal static class HardenerWireDefinitions
{
    internal const string ShieldEffectName = "modifyActiveShieldResonancePostPercent";
    internal const string ArmorEffectName = "modifyActiveArmorResonancePostPercent";

    internal static string Graphic(DogmaEffect effect) =>
        effect == DogmaEffect.ActiveShieldResistance ? "effects.ModifyShieldResonance" : "effects.ArmorHardening";
}
