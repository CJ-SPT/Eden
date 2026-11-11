using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerPopulationStore
{
    internal static void AddAgent(WorldAction world, CapsuleerAgent agent)
    {
        if (world.Set<SavedCapsuleerAgent>().Find(agent.PilotId) is not null)
        {
            return;
        }
        world.Add(
            new SavedCapsuleerAgent
            {
                PilotId = agent.PilotId,
                CommunitySystemId = agent.CommunitySystemId,
                Career = agent.Career,
                FounderShipId = agent.FounderShipId,
                FounderModuleId = agent.FounderModuleId,
                StartingCents = agent.StartingCents,
                Slot = agent.Slot,
            }
        );
    }
}
