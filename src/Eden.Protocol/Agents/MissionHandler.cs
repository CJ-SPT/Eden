using System.Globalization;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
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
internal sealed partial class MissionHandler(
    IPilotMissions missions,
    IPilotResearch research,
    INpcDirectory directory,
    IStationDirectory stations,
    IPilotInteractionHistory history,
    LoyaltyWalletHandler loyalty,
    CooperativeFleetHandler fleets,
    TimeProvider clock
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
        if (!bound && service != "agentMgr")
        {
            return PilotCallResult.Unsupported;
        }
        if (!bound && service == "agentMgr" && method is not ("GetMyJournalDetails" or "GetDisabledMissions"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            long pilotId = context.Pilot.PilotId;
            // Build 3662168 Opportunities reads objectives with ignoreLocateCheck=True.
            // The flag changes no authority: charID must still identify the selected pilot.
            if (
                keywords is { Count: > 0 }
                && (
                    !bound
                    || method != "GetMissionObjectiveInfo"
                    || keywords.Any(pair =>
                        pair.Key switch
                        {
                            "ignoreLocateCheck" => pair.Value is not bool,
                            "charID" => pair.Value is not null && (pair.Value is not long character || character != pilotId),
                            _ => true,
                        }
                    )
                )
            )
            {
                throw new InvalidDataException("Unexpected mission keyword arguments.");
            }
            if (!bound)
            {
                return method switch
                {
                    "GetMyJournalDetails" when args.Length == 0 => new MissionJournal(
                        missions.Journal(pilotId),
                        research.Research(pilotId)
                    ),
                    "GetDisabledMissions" when args.Length == 0 => BlueEmpty.List,
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
                return new MissionJournal(missions.Journal(pilotId, agentId), research.Research(pilotId, agentId));
            }
            if (method == "GetInfoServiceDetails")
            {
                return args.Length == 0
                    ? new AgentServiceDetailsReply(missions.Information(pilotId, agentId))
                    : new PilotCallResult.Failure("Read agent information without additional arguments.");
            }
            if (method is "IsCheatingWithAgent" or "ShouldAlwaysAllowReplay" && args.Length == 0)
            {
                // Build 3662168's mission three-dot menu reads both developer flags
                // before displaying its entries. Eden has no mission cheat overrides.
                return false;
            }
            if (method == "GetDungeonShipRestrictions")
            {
                if (
                    args is not ([long] or [long, null or long])
                    || (long)args[0]! is <= 0 or > int.MaxValue
                    || args.Length == 2 && args[1] is long gate && gate <= 0
                )
                {
                    throw new InvalidDataException("Choose a mission dungeon and optional catalog gate ID.");
                }
                return new MissionShipRestrictionsReply(
                    missions.ShipRestrictions(pilotId, agentId, checked((int)(long)args[0]!), args.Length == 2 ? (long?)args[1] : null)
                );
            }
            if (method == "GetEntryPoint")
            {
                // Epic-arc previews ask before an offer exists. Only an accepted
                // mission's resident entrance is exposed; absent/expired sites return None.
                return args.Length != 0 ? new PilotCallResult.Failure("Read the mission entrance without additional arguments.")
                    : missions.EntryPoint(pilotId, agentId) is { } position ? new MissionEntryPoint(position)
                    : PilotCallResult.None;
            }
            if (method == "GetAgentLocationWrap" && args.Length == 0)
            {
                if (agent.Space is not null)
                {
                    return new SpaceAgentLocation(agent);
                }
                // Build 3662168 AgentDialogueWindow.GetAgentHeader reads this before
                // rendering the briefing, independently of whether an offer exists.
                var station = stations.Read().Stations.FirstOrDefault(s => s.Id == agent.StationId);
                return station is null
                    ? new PilotCallResult.Failure("This agent has no supported station location.")
                    : new AgentLocation(station);
            }
            if (method == "GetReplayTimestamp")
            {
                // Build 3662168 dockedUI.AgentsPanel calls Agents.GetAgentTimers
                // for every row before rendering it.
                if (args is not [long timerPilot] || timerPilot != pilotId)
                {
                    return new PilotCallResult.Failure("Read only your own agent replay timestamp.");
                }
                long? replayAt = agent.Type switch
                {
                    AgentType.Heraldry => missions.Information(pilotId, agentId).ReplayAt,
                    AgentType.EpicArc => missions.EpicArcReplayAt(pilotId, agentId),
                    _ => null,
                };
                if (replayAt is long timestamp)
                {
                    return timestamp;
                }
                return PilotCallResult.None;
            }
            if (method == "DoAction" && args is [var action] && action is null or long)
            {
                if (agent.Type == AgentType.Research)
                {
                    return ResearchActionCall(context, agentId, action);
                }
                if (action is null)
                {
                    if (agent.Type == AgentType.Heraldry && missions.Information(pilotId, agentId).ReplayAt is long replayAt)
                    {
                        return ParagonCooldown(null, replayAt);
                    }
                    if (agent.Type == AgentType.EpicArc && missions.ArriveEpicArcAgent(pilotId, agentId) is { } arrival)
                    {
                        loyalty.Enter(context);
                        Notify(context, arrival.Offer?.Agent.Id ?? agentId, arrival);
                    }
                    var current = missions.Current(pilotId, agentId);
                    return Conversation(pilotId, agent, current, "");
                }
                long token = (long)action;
                if (token < 0)
                {
                    throw new InvalidDataException("Invalid mission action token.");
                }
                if (EpicArcChoiceAction.Read(token) is { } selected)
                {
                    if (agent.Type != AgentType.EpicArc)
                    {
                        throw new InvalidDataException("Choose a branch from an active epic arc offer.");
                    }
                    loyalty.Enter(context);
                    var update = missions.ChooseEpicArc(pilotId, agentId, selected.OfferId, selected.MissionId);
                    Notify(context, agentId, update);
                    return Conversation(pilotId, agent, update.Offer, update.Event, "Request a mission from this agent.");
                }
                long instanceId = token / MissionRules.ActionStride;
                var operation = (MissionAction)(token % MissionRules.ActionStride);
                if (operation is not (MissionAction.Request or MissionAction.View or MissionAction.Defer) && instanceId == 0)
                {
                    throw new InvalidDataException("Use the action from the current mission offer.");
                }
                PilotCallResult Finish(bool share)
                {
                    loyalty.Enter(context);
                    var update = share
                        ? missions.CompleteWithFleet(pilotId, agentId, instanceId)
                        : missions.Act(pilotId, agentId, operation, instanceId == 0 ? null : instanceId);
                    Notify(context, agentId, update);
                    if (
                        agent.Type == AgentType.Heraldry
                        && update.Event == "completed"
                        && missions.Information(pilotId, agentId).ReplayAt is long replayAt
                    )
                    {
                        return ParagonCooldown(update.Offer, replayAt);
                    }
                    return Conversation(pilotId, agent, update.Offer, update.Event, "Request a mission from this agent.");
                }
                if (operation == MissionAction.Complete && missions.CanShareReward(pilotId, agentId, instanceId))
                {
                    return new PilotCallResult.ClientCall(
                        new ResearchDialog(
                            "SingleChoiceBox",
                            [
                                "",
                                FleetRewardLabel("CompleteShareWithFleetMessage"),
                                new List<object?> { FleetRewardLabel("ShareWithFleetYes"), FleetRewardLabel("ShareWithFleetNo") },
                                agentId,
                                missions.Current(pilotId, agentId)?.MissionId,
                                null,
                            ],
                            [],
                            answer =>
                                ResearchDialog.Choice(answer, 2) is int choice
                                    ? Finish(choice == 0)
                                    : new MissionConversation(missions.Current(pilotId, agentId), "", "Mission turn-in cancelled.")
                        )
                    );
                }
                return Finish(false);
            }
            var offer = missions.Current(pilotId, agentId);
            if (method == "GetMissionBriefingInfo" && args.Length == 0)
            {
                return offer is null ? PilotCallResult.None : new MissionBriefing(offer, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            }
            if (method == "GetMissionObjectiveInfo" && args.Length == 0)
            {
                return offer is null ? BlueEmpty.Dictionary : new MissionObjectives(offer, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
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
                return offer is null ? PilotCallResult.None : new MissionJournalInfo(offer, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
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
                    || context.Pilot.Position is null
                )
                {
                    throw new InvalidDataException("Choose a mission warp from space.");
                }
                string location = BlueRead.Text(args[0]);
                int system = context.Pilot.SystemId;
                double range = Convert.ToDouble(distance);
                FlightCommand warp = offer switch
                {
                    { State: MissionState.Accepted, Kind: MissionKind.Combat or MissionKind.Mining, RequiresScanner: false }
                        when location == "dungeon" && offer.SystemId == system => new FlightCommand.WarpToPoint(
                        MissionRules.FirstSiteId + offer.Id,
                        offer.Position,
                        range
                    ),
                    { State: MissionState.Accepted, Kind: MissionKind.AgentHandoff, EpicArcTargetAgentId: long destination }
                        when location is "objective" or "objective.destination" && offer.EpicArcTargetSystem == system =>
                        new FlightCommand.WarpTo(offer.EpicArcTargetStation is int station ? station : destination, range),
                    _ => throw new InvalidDataException("Warp to your accepted mission destination in its solar system."),
                };
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
        if (
            (offer.Storyline || offer.Rewards.FactionFraction > 0)
            && (offer.EpicArcStandingFactionId ?? offer.Agent.FactionId) is int faction
        )
        {
            gains[faction] = (10 - current.GetValueOrDefault(faction)) * offer.Rewards.FactionFraction;
        }
        return new MissionStandingGains(gains);
    }

    private static object?[] FleetRewardLabel(string name) => [$"UI/Agents/StandardMission/{name}", BlueEmpty.Dictionary];

    private MissionConversation Conversation(long pilotId, CatalogAgent agent, MissionOffer? offer, string action, string? greeting = null)
    {
        bool epic = agent.Type == AgentType.EpicArc;
        bool gated = epic || agent.Type == AgentType.Career;
        var access = gated || greeting is null ? missions.Access(pilotId, agent.Id) : null;
        long? replayDelay =
            epic && missions.EpicArcReplayAt(pilotId, agent.Id) is long replay
                ? replay - clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()
                : null;
        return new(offer, action, greeting ?? access!.Reason)
        {
            SuppressRequest = gated && access?.Allowed == false,
            ReplayDelay = replayDelay is > 0 ? replayDelay : null,
            EpicChoices =
                epic && offer is { Kind: MissionKind.Choice, State: MissionState.Accepted }
                    ? missions.EpicArcChoices(pilotId, agent.Id)
                    : [],
        };
    }

    private MissionConversation ParagonCooldown(MissionOffer? offer, long replayAt) =>
        new(offer, "", "IRIS is on cooldown.") { ReplayDelay = Math.Max(1, replayAt - clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()) };

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
        if (update.BalanceCents is long balance)
        {
            context.Notifications.Add(new AccountBalanceChanged(context.Pilot.PilotId, balance / (double)MarketDefinitions.CentsPerIsk));
        }
        if (update.Offer is { } offer && (update.BalanceCents is not null || update.PointsBefore != update.PointsAfter))
        {
            loyalty.Notify(context, offer.Agent.CorporationId, update.PointsBefore, update.PointsAfter);
            if (update.CorporationPointsAfter is long points)
            {
                loyalty.NotifyCorporation(context, offer.Agent.CorporationId, points);
            }
        }
        if (update.BalanceCents is not null || update is { Event: "completed", Offer.Agent.Type: AgentType.Research })
        {
            foreach (var storyline in missions.Journal(context.Pilot.PilotId).Where(m => m.Storyline && m.State == MissionState.Offered))
            {
                context.Notifications.Add(new AgentMissionChanged("offered", storyline.Agent.Id));
            }
        }
        if ((update.Event.Length > 0 || update.Continuation is not null) && update.Offer?.EpicArcId is not null)
        {
            context.Notifications.Add(new EpicArcJournalChanged());
        }
        if (update.Event.Length > 0)
        {
            context.Notifications.Add(new AgentMissionChanged(update.Event, agentId));
            if (update.Offer?.Agent.Type == AgentType.Research)
            {
                context.Notifications.Add(new ResearchChanged("research_update_ppd"));
            }
        }
        foreach (long retired in update.RetiredAgentIds)
        {
            context.Notifications.Add(new AgentMissionChanged("offer_removed", retired));
        }
        if (update.Continuation is { } continuation)
        {
            context.Notifications.Add(new AgentMissionChanged("offered", continuation.Agent.Id));
        }
    }
}

internal sealed record MissionStandingGains(IReadOnlyDictionary<long, double> Gains) : BlueModel
{
    public override object ToBlue() => Gains.ToDictionary(p => p.Key, p => (object?)p.Value);
}
