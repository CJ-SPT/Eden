using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;

namespace Eden.Gameplay.Loot;

public sealed record LootRights(long OwnerId, int CorporationId, bool Abandoned);

public sealed record LootOwnerAccess(CrimewatchFlag Flag, double SecurityStatus, bool LimitedEngagement);

public interface ISpaceLootRights
{
    void Abandon(long pilotId, IReadOnlyList<long> containerIds, long now);
}

public static class LootRightsRules
{
    public const int MaximumAbandonSelection = 1000;

    public static bool CanTakeFromOwner(LootOwnerAccess owner) =>
        owner.Flag is CrimewatchFlag.Suspect or CrimewatchFlag.Criminal
        || owner.SecurityStatus <= CrimewatchRules.OutlawSecurity
        || owner.LimitedEngagement;

    public static bool AbandonedAtCreation(LootOwnerAccess owner, bool playerWreck) =>
        owner.Flag == CrimewatchFlag.Criminal || playerWreck && owner.SecurityStatus <= CrimewatchRules.OutlawSecurity;

    public static bool CanTake(long pilotId, int corporationId, LootRights rights, bool fleetAccess) =>
        rights.Abandoned
        || rights.OwnerId == pilotId
        || fleetAccess
        || corporationId >= CorporationRules.FirstCorporationId && corporationId == rights.CorporationId;

    public static bool CheckTake(bool entitled, double security, ShipSafetyLevel safety, bool alpha = false)
    {
        if (!double.IsFinite(security) || !Enum.IsDefined(safety))
        {
            throw new InvalidDataException("Invalid loot access inputs.");
        }
        if (entitled || security <= 0)
        {
            return false;
        }
        if (safety == ShipSafetyLevel.Full || alpha && security >= CrimewatchRules.HighSecurity)
        {
            throw new InvalidDataException("Your safety setting prevents stealing these contents.");
        }
        return true;
    }

    public static CrimewatchTimers Theft(CrimewatchTimers timers, long now) =>
        timers.Criminal.Running(now)
            ? timers with
            {
                Criminal = CrimewatchRules.Extend(timers.Criminal, now, CrimewatchRules.FlagDuration),
            }
            : timers with
            {
                Suspect = CrimewatchRules.Extend(timers.Suspect, now, CrimewatchRules.FlagDuration),
            };
}
