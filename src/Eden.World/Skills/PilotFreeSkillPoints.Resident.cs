using Eden.Data.World.Characters;
using Eden.Data.World.Skills;
using Eden.Gameplay.Skills;
using Eden.World.Runtime.Economy;

namespace Eden.World.Skills;

internal sealed partial class PilotTrainingStore
{
    internal static void CreditSilentFreePoints(WorldAction world, long pilotId, long amount)
    {
        long balance = ReadFreePoints(world, pilotId);
        SavePointBalance(world, pilotId, FreeSkillPointRules.Credit(balance, amount), advanceRevision: false);
    }

    internal static long ReadFreePoints(WorldAction world, long pilotId) => ReadPointBalance(world, pilotId).Points;

    private static void SavePointBalance(WorldAction world, long pilotId, long balance, bool advanceRevision = true)
    {
        var state = world.Set<SavedFreeSkillPoints>().Find(pilotId) ?? world.Add(new SavedFreeSkillPoints { PilotId = pilotId }).Entity;
        state.Points = balance;
        // Pool-only activity credits stay silent. Revisions request skill/brain/queue notifications.
        if (advanceRevision)
        {
            state.Revision = checked(state.Revision + 1);
        }
    }

    private static FreeSkillPointBalance ReadPointBalance(WorldAction world, long pilotId)
    {
        if (!world.Filter<Pilot>(p => p.Id == pilotId).Any())
        {
            throw new InvalidDataException("Unknown skill point recipient.");
        }
        var state = world.Set<SavedFreeSkillPoints>().Find(pilotId);
        return state is null ? new(0, 0)
            : state.Points >= 0 && state.Revision >= 0 ? new(state.Points, state.Revision)
            : throw new InvalidDataException("Invalid unallocated skill point balance.");
    }
}
