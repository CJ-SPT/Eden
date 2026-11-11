using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    internal JourneyInput? CaptureJourney(WorldAction world, CapsuleerJob job)
    {
        if (!_loaded || _pilot is null || _work is null || job.Action != CapsuleerAction.Trade)
        {
            return null;
        }
        var agent = world.Set<SavedCapsuleerAgent>().Find(pilotId)!;
        return JourneyInputs(agent, job.DueTicks);
    }
}
