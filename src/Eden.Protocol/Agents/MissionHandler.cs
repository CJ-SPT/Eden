using System.Globalization;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Characters;
using Eden.Protocol.Docking;
using Eden.Protocol.Fleets;
using Eden.Protocol.Inventory;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Agents;

[Injectable(InjectionType.Scoped)]
internal sealed class MissionHandler(
    IPilotMissions missions,
    INpcDirectory directory,
    IPilotInteractionHistory history,
    LoyaltyWalletHandler loyalty,
    CooperativeFleetHandler fleets
) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool bound = service.StartsWith("@agent:", StringComparison.Ordinal);
        if (!bound && service != "agentMgr" && service != "missionTrackerMgr")
        {
            return PilotCallResult.Unsupported;
        }
        if (
            !bound
            && service == "agentMgr"
            && method is not ("GetMyJournalDetails" or "GetDisabledMissions" or "GetSolarSystemOfAgent" or "GetCompletedCareerAgentIDs")
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected mission keyword arguments.");
            }
            long pilotId = context.Pilot.PilotId;
            if (service == "missionTrackerMgr")
            {
                // The legacy tracker has its own server event language. Ordinary native
                // objective cards use GetMissionObjectiveInfo instead of invented tracker tokens.
                return method == "GetMissionInfoItems" && args is [long id] && directory.Agent(id) is not null
                    ? PilotCallResult.None
                    : new PilotCallResult.Failure("Invalid mission tracker request.");
            }
            if (!bound)
            {
                return method switch
                {
                    "GetMyJournalDetails" when args.Length == 0 => new MissionJournal(missions.Journal(pilotId)),
                    "GetDisabledMissions" when args.Length == 0 => BlueEmpty.List,
                    "GetCompletedCareerAgentIDs" when args is [List<object?>] => BlueEmpty.List,
                    "GetSolarSystemOfAgent" when args is [long id] && directory.Agent(id)?.SystemId is int system => system,
                    _ => new PilotCallResult.Failure("Invalid agent mission directory request."),
                };
            }
            if (
                !long.TryParse(service.AsSpan("@agent:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out long agentId)
                || directory.Agent(agentId) is not { } agent
            )
            {
                throw new InvalidDataException("Unknown bound NPC agent.");
            }
            if (method == "GetMyJournalDetails" && args.Length == 0)
            {
                return new MissionJournal(missions.Journal(pilotId, agentId));
            }
            if (method == "GetReplayTimestamp")
            {
                // Build 3662168 dockedUI.AgentsPanel calls Agents.GetAgentTimers
                // for every row before rendering it. Ordinary missions have no replay lock.
                return args is [long timerPilot] && timerPilot == pilotId
                    ? PilotCallResult.None
                    : new PilotCallResult.Failure("Read only your own agent replay timestamp.");
            }
            if (method == "DoAction" && args is [var action] && action is null or long)
            {
                if (action is null)
                {
                    var current = missions.Current(pilotId, agentId);
                    return new MissionConversation(current, "", missions.Access(pilotId, agentId).Reason);
                }
                long token = (long)action;
                if (token < 0)
                {
                    throw new InvalidDataException("Invalid mission action token.");
                }
                long instanceId = token / MissionRules.ActionStride;
                var operation = (MissionAction)(token % MissionRules.ActionStride);
                if (operation is not (MissionAction.Request or MissionAction.View or MissionAction.Defer) && instanceId == 0)
                {
                    throw new InvalidDataException("Use the action from the current mission offer.");
                }
                loyalty.Enter(context);
                var update = missions.Act(pilotId, agentId, operation, instanceId == 0 ? null : instanceId);
                Notify(context, agentId, update);
                return new MissionConversation(update.Offer, update.Event, "Request a mission from this agent.");
            }
            var offer = missions.Current(pilotId, agentId);
            if (method == "GetMissionBriefingInfo" && args.Length == 0)
            {
                return offer is null ? PilotCallResult.None : new MissionBriefing(offer);
            }
            if (method == "GetMissionObjectiveInfo" && args.Length == 0)
            {
                return offer is null ? BlueEmpty.Dictionary : new MissionObjectives(offer);
            }
            if (method == "GetMissionKeywords" && args is [long content] && offer?.MissionId == content)
            {
                return new MissionKeywords(offer);
            }
            if (
                method == "GetMissionJournalInfo"
                && args is [var character, var contentId]
                && character is null or long
                && contentId is null or long
                && (character is null || Equals(character, pilotId))
                && (contentId is null || Equals(contentId, (long?)offer?.MissionId))
            )
            {
                return offer is null ? PilotCallResult.None : new MissionJournalInfo(offer);
            }
            if (method == "GetStandingGainsForMission" && args is [long missionId] && offer?.MissionId == missionId)
            {
                return StandingGains(pilotId, offer);
            }
            if (method == "RemoveOfferFromJournal" && args.Length == 0 && offer?.State == MissionState.Offered)
            {
                var update = missions.Act(pilotId, agentId, MissionAction.Decline, offer.Id);
                Notify(context, agentId, update);
                return PilotCallResult.None;
            }
            if (method == "WarpToLocation")
            {
                if (
                    args is not [string or byte[], 0L, var distance, false or 0L or true or 1L, var referringAgent, var formation]
                    || distance is not (double or long)
                    || referringAgent is not null && !Equals(referringAgent, agentId)
                    || BlueMarshal.Text(args[0]) != "dungeon"
                    || offer is not { Kind: MissionKind.Combat, State: MissionState.Accepted }
                    || offer.SystemId != context.Pilot.SystemId
                    || offer.ObjectiveComplete
                    || context.Pilot.Position is null
                )
                {
                    throw new InvalidDataException("Warp to your accepted combat mission in its solar system.");
                }
                var warp = new FlightCommand.WarpToPoint(MissionRules.FirstSiteId + offer.Id, offer.Position, Convert.ToDouble(distance));
                if (args[3] is true or 1L)
                {
                    return fleets.Warp(context, warp, formation);
                }
                fleets.CancelWarpOrders(pilotId);
                return context.Flight.Execute(warp);
            }
            return new PilotCallResult.Failure("Invalid or unsupported agent mission request.");
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("Mission reward or action exceeds the supported limit.");
        }
    }

    private BlueModel StandingGains(long pilotId, MissionOffer offer)
    {
        var current = history.Standings(pilotId).ToDictionary(s => s.SourceId, s => s.Value);
        Dictionary<long, double> gains = new()
        {
            [offer.Agent.Id] = (10 - current.GetValueOrDefault(offer.Agent.Id)) * offer.Rewards.CorporationFraction,
            [offer.Agent.CorporationId] = (10 - current.GetValueOrDefault(offer.Agent.CorporationId)) * offer.Rewards.CorporationFraction,
        };
        if (offer.Storyline && offer.Agent.FactionId is int faction)
        {
            gains[faction] = (10 - current.GetValueOrDefault(faction)) * offer.Rewards.FactionFraction;
        }
        return new MissionStandingGains(gains);
    }

    private void Notify(PilotCallContext context, long agentId, MissionUpdate update)
    {
        foreach (var change in update.Items)
        {
            InventoryChangesWire.NotifyItemChange(context, change);
        }
        foreach (var standing in update.Standings)
        {
            context.Notifications.Add(new NpcStandingSet(standing.SourceId, context.Pilot.PilotId, standing.After));
        }
        if (update.BalanceCents is long balance && update.Offer is { } offer)
        {
            context.Notifications.Add(new AccountBalanceChanged(context.Pilot.PilotId, balance / (double)MarketDefinitions.CentsPerIsk));
            loyalty.Notify(context, offer.Agent.CorporationId, update.PointsBefore, update.PointsAfter);
            foreach (var storyline in missions.Journal(context.Pilot.PilotId).Where(m => m.Storyline && m.State == MissionState.Offered))
            {
                context.Notifications.Add(new AgentMissionChanged("offered", storyline.Agent.Id));
            }
        }
        if (update.Event.Length > 0)
        {
            context.Notifications.Add(new AgentMissionChanged(update.Event, agentId));
        }
    }
}

internal sealed record MissionStandingGains(IReadOnlyDictionary<long, double> Gains) : BlueModel
{
    public override object ToBlue() => Gains.ToDictionary(p => p.Key, p => (object?)p.Value);
}
