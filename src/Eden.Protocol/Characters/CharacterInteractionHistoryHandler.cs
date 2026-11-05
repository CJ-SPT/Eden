using Eden.Gameplay.Characters;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class CharacterInteractionHistoryHandler(IPilotInteractionHistory history) : ISelectedPilotCallHandler
{
    public void NotifyDestruction(PilotCallContext context)
    {
        context.Notifications.Add(new CombatHistoryChanged());
        foreach (var standing in history.Standings(context.Pilot.PilotId))
        {
            context.Notifications.Add(new NpcStandingSet(standing.SourceId, context.Pilot.PilotId, standing.Value));
        }
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service == "charMgr" && method == "GetRecentShipKillsAndLosses")
        {
            // Build 3662168 Info.GetKillsRecentKills/GetKillsRecentLosses supplies
            // (num, startIndex), initially (25, None). There is no target pilot ID.
            if (
                keywords is { Count: > 0 }
                || args is not [long count, var cursor]
                || count is < 1 or > InteractionHistoryRules.MaximumCombatPageSize
                || cursor is not (null or (long and >= 0))
            )
            {
                return new PilotCallResult.Failure("Invalid combat history request.");
            }
            return new ShipCombatHistoryReply(history.Combat(context.Pilot.PilotId, checked((int)count), cursor as long?));
        }
        if (service == "standingMgr" && method == "GetStandingTransactions")
        {
            // Standing.GetStandingTransactions reads personal and corporation
            // histories. Keep other pilots' histories outside this private call.
            if (
                keywords is { Count: > 0 }
                || args is not [long source, long destination]
                || source <= 0
                || (destination != context.Pilot.PilotId && destination != context.Pilot.CorpId)
            )
            {
                return new PilotCallResult.Failure("Invalid standing history request.");
            }
            return new StandingTransactionHistoryReply(
                destination == context.Pilot.PilotId ? history.Transactions(destination, source) : []
            );
        }
        if (service == "standingMgr" && method is "GetCharStandings" or "GetCorpStandings")
        {
            if (args.Length != 0 || keywords is { Count: > 0 })
            {
                return new PilotCallResult.Failure("Invalid standings request.");
            }
            return new CharacterStandingsReply(
                context.Pilot.PilotId,
                method == "GetCharStandings" ? history.Standings(context.Pilot.PilotId) : []
            );
        }
        return PilotCallResult.Unsupported;
    }
}
