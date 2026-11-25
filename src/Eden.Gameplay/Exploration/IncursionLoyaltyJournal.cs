namespace Eden.Gameplay.Exploration;

// Build 3662168 appLogConst reward event identities.
public enum IncursionLoyaltyEvent
{
    Stored = 312,
    Paid = 313,
    Forfeited = 314,
}

public sealed record IncursionLoyaltyEntry(
    long TaleId,
    int SystemId,
    int RewardId,
    int RewardMessageId,
    string DungeonName,
    int Pilots,
    long Time,
    IncursionLoyaltyEvent Event,
    long Points
);

public interface IIncursionLoyaltyJournal
{
    IReadOnlyList<IncursionLoyaltyEntry> History(long pilotId);
}
