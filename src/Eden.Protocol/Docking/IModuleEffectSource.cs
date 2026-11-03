using Eden.Gameplay.Docking;

namespace Eden.Protocol.Docking;

internal interface IModuleEffectSource
{
    DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module);
}
