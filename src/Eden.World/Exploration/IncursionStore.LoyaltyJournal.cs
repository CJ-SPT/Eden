using Eden.Data.World.Characters;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Exploration;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Exploration;

internal sealed partial class IncursionStore
{
    public IReadOnlyList<IncursionLoyaltyEntry> History(long pilotId)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return [];
        }
        return live.Economy.Inspect(world =>
            ((EconomySet<SavedIncursionLoyaltyEntry, EconomyIncursionLoyaltyEntry>)world.Set<SavedIncursionLoyaltyEntry>())
                .IndexedSnapshots(nameof(SavedIncursionLoyaltyEntry.PilotId), pilotId)
                .OrderByDescending(row => row.Entry.Time)
                .ThenBy(row => row.Entry.Event)
                .ThenByDescending(row => row.SourceId)
                .Select(row => row.Entry)
                .ToArray()
        );
    }

    private static IEnumerable<(long PilotId, long Points)> HeldLoyalty(WorldAction world, IEnumerable<SavedIncursionContribution> rows) =>
        rows.Where(row => row.PendingLoyalty > 0)
            .GroupBy(row => row.PilotId)
            .Where(group => world.PilotExists(group.Key) && world.Set<SavedCharacterDeletion>().Find(group.Key)?.DeletedAt is null)
            .Select(group => (group.Key, group.Sum(row => row.PendingLoyalty)));

    private static void RecordLoyaltyClose(
        WorldAction world,
        long pilotId,
        long taleId,
        int systemId,
        IncursionLoyaltyEvent outcome,
        long points,
        long now
    ) =>
        world.Add(
            new SavedIncursionLoyaltyEntry
            {
                PilotId = pilotId,
                // Positive source IDs name site awards. A campaign has one terminal row per pilot.
                SourceId = -taleId,
                TaleId = taleId,
                SystemId = systemId,
                RewardId = (int)RewardGroup.Mothership,
                RewardMessageId = RewardLogName(RewardGroup.Mothership),
                Event = outcome,
                Points = points,
                Time = now,
            }
        );

    // Pinned grouprewards log names, also used by terminal rows with no award site.
    private static int RewardLogName(RewardGroup group) =>
        group switch
        {
            RewardGroup.Vanguard => 545502,
            RewardGroup.Assault => 545503,
            RewardGroup.Headquarters => 545504,
            RewardGroup.Scout => 545505,
            RewardGroup.Mothership => 545506,
            _ => throw new ArgumentOutOfRangeException(nameof(group)),
        };
}
