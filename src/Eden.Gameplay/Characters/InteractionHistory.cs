using System.Collections.Immutable;

namespace Eden.Gameplay.Characters;

public sealed record CombatParty(
    long EntityId,
    long? CharacterId,
    int CorporationId,
    int? FactionId,
    int? ShipTypeId,
    int? WeaponTypeId,
    double? SecurityStatus,
    double Damage,
    int? AllianceId = null
);

public sealed record CombatLostItem(
    int TypeId,
    int Flag,
    bool Singleton,
    int Dropped,
    int Destroyed,
    IReadOnlyList<CombatLostItem> Contents
);

public sealed record ShipCombatReport(
    long Id,
    long PilotId,
    long Time,
    int SystemId,
    CombatParty Victim,
    CombatParty FinalBlow,
    IReadOnlyList<CombatParty> OtherAttackers,
    IReadOnlyList<CombatLostItem> Items,
    bool Historical = false,
    long? KillRightSupplierId = null
);

public enum StandingEvent
{
    // Build 3662168 appLogConst.eventStandingAgentMissionCompleted.
    AgentMissionCompleted = 73,

    // Build 3662168 appLogConst.eventStandingCombatShipKill.
    CombatShipKill = 77,

    AgentMissionFailed = 74,
    AgentMissionDeclined = 75,

    // Build 3662168 appLogConst.eventStandingDerivedModificationPositive / Negative.
    DerivedModificationPositive = 82,
    DerivedModificationNegative = 83,

    PromotionFactionIncrease = 216,

    // Build 3662168 appLogConst.eventStandingCombatAggressionOwnFaction.
    CombatAggressionOwnFaction = 225,
    TacticalSiteDefended = 229,
    TacticalSiteConquered = 230,
}

public sealed record NpcStanding(long SourceId, double Value);

public sealed record StandingTransaction(
    long Id,
    long PilotId,
    long SourceId,
    long Time,
    StandingEvent Event,
    double Before,
    double After,
    int VictimCorporationId,
    int SystemId,
    int VictimTypeId
)
{
    public int? MissionTitleId { get; init; }
}

public interface IPilotInteractionHistory
{
    IReadOnlyList<ShipCombatReport> Combat(long pilotId, int count, long? beforeId);
    ShipCombatReport? Report(long pilotId, long reportId);
    IReadOnlyList<NpcStanding> Standings(long pilotId);
    IReadOnlyList<StandingTransaction> Transactions(long pilotId, long sourceId);
}

public static class StandingRules
{
    public const double Minimum = -10;
    public const double Maximum = 10;

    public static double FactionLoss(double current, double fraction)
    {
        if (!double.IsFinite(current) || current is < Minimum or > Maximum || !double.IsFinite(fraction) || fraction is < 0 or > 1)
        {
            throw new InvalidDataException("Invalid NPC standing or faction-loss fraction.");
        }
        // entityFactionLoss (562) is a fraction of the distance to -10.
        return Math.Max(Minimum, current + (Minimum - current) * fraction);
    }
}

public static class InteractionHistoryRules
{
    public const int MaximumCombatPageSize = 1000;

    public static IReadOnlyDictionary<long, CombatParty> AddDamage(
        IReadOnlyDictionary<long, CombatParty> previous,
        CombatParty attacker,
        double damage
    )
    {
        if (!(damage > 0))
        {
            return previous;
        }
        var contributions = previous.ToImmutableDictionary();
        return contributions.SetItem(
            attacker.EntityId,
            attacker with
            {
                Damage = (previous.GetValueOrDefault(attacker.EntityId)?.Damage ?? 0) + damage,
            }
        );
    }
}
