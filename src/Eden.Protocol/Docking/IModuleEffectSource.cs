using Eden.Gameplay.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Docking;

internal interface IModuleEffectSource
{
    void CancelModule(PilotCallContext context, long module);

    DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module);
}
