using Eden.Gameplay.Agents;
using Eden.Gameplay.Market;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Agents;

internal sealed partial class MissionHandler
{
    private PilotCallResult ResearchActionCall(PilotCallContext context, long agentId, object? action)
    {
        long pilotId = context.Pilot.PilotId;
        ResearchProject? Current() => research.Research(pilotId, agentId).SingleOrDefault();
        MissionOffer? ActiveOffer() =>
            missions.Current(pilotId, agentId) is { State: MissionState.Offered or MissionState.Accepted } offer ? offer : null;
        ResearchConversation Conversation(ResearchProject? current, string message, string eventName = "") =>
            new(current, message, current is null ? null : ActiveOffer(), eventName);
        void Changed(string name) => context.Notifications.Add(new ResearchChanged(name));
        if (action is null)
        {
            var current = Current();
            return Conversation(
                current,
                current is null
                    ? "Choose Start Research to select a science field."
                    : $"Research points: {current.Points:0.00}. Daily research: {current.PointsPerDay:0.00} RP. "
                        + "Each datacore costs 100 RP and 10,000 ISK."
            );
        }
        switch (action)
        {
            case (long)ResearchAction.Start:
                var fields = research.Fields(pilotId, agentId);
                if (fields.Count == 0)
                {
                    return new PilotCallResult.Failure("Train one of this agent's science fields to the agent's level.");
                }
                return new PilotCallResult.ClientCall(
                    new ResearchDialog(
                        "SingleChoiceBox",
                        ["Select Research Type", "Choose a science field.", fields.Select(f => (object?)f.Name).ToList(), agentId],
                        [],
                        answer =>
                        {
                            if (ResearchDialog.Choice(answer, fields.Count) is not int index)
                            {
                                return Conversation(Current(), "Research selection cancelled.");
                            }
                            _ = research.StartResearch(pilotId, agentId, fields[index].SkillId);
                            Changed("research_started");
                            return Conversation(Current(), "Research has started.");
                        }
                    )
                );
            case (long)ResearchAction.Cancel:
                var cancelled = Current() ?? throw new InvalidDataException("This agent has no active research project.");
                string cancellation = ActiveOffer() is not null
                    ? "Cancel this project and end the open mission? All unspent research points with this agent will be lost."
                    : "Cancel this project? All unspent research points with this agent will be lost.";
                return new PilotCallResult.ClientCall(
                    new ResearchDialog(
                        "YesNo",
                        ["Cancel Research", cancellation, agentId],
                        [],
                        answer =>
                        {
                            if (answer is not true)
                            {
                                return Conversation(Current(), "Research continues.");
                            }
                            string? missionEvent = research.CancelResearch(pilotId, agentId, cancelled.Id) switch
                            {
                                MissionState.Declined => "offer_declined",
                                MissionState.Quit => "quit",
                                MissionState.Failed => "failed",
                                _ => null,
                            };
                            if (missionEvent is not null)
                            {
                                context.Notifications.Add(new AgentMissionChanged(missionEvent, agentId));
                            }
                            Changed("research_update_ppd");
                            return Conversation(
                                null,
                                "Research cancelled. Unspent research points have been discarded.",
                                missionEvent ?? ""
                            );
                        }
                    )
                );
            case (long)ResearchAction.Buy:
                var project = Current() ?? throw new InvalidDataException("Start research before buying datacores.");
                int maximum = (int)Math.Min(int.MaxValue, Math.Floor(project.Points / ResearchRules.PointsPerDatacore));
                if (maximum == 0)
                {
                    return Conversation(project, "You need at least 100 research points to buy a datacore.");
                }
                return new PilotCallResult.ClientCall(
                    new ResearchDialog(
                        "GetQuantity",
                        [],
                        new()
                        {
                            ["maxvalue"] = maximum,
                            ["minvalue"] = 1,
                            ["setvalue"] = maximum,
                            ["digits"] = 0,
                            ["caption"] = "Buy Datacores",
                            ["label"] = "Datacores (100 RP and 10,000 ISK each)",
                        },
                        answer =>
                        {
                            if (answer is null)
                            {
                                return Conversation(Current(), "Datacore purchase cancelled.");
                            }
                            if (answer is not long quantity || quantity <= 0 || quantity > maximum)
                            {
                                throw new InvalidDataException("Choose a valid whole datacore quantity.");
                            }
                            var purchase = research.BuyDatacores(pilotId, agentId, project.Id, checked((int)quantity));
                            InventoryChangesWire.NotifyItemChange(context, purchase.Item);
                            context.Notifications.Add(
                                new AccountBalanceChanged(pilotId, purchase.BalanceCents / (double)MarketDefinitions.CentsPerIsk)
                            );
                            Changed("research_update_ppd");
                            return Conversation(Current(), "Datacores delivered to your Item Hangar.");
                        }
                    )
                );
            default:
                if (action is not long token || token < 0)
                {
                    return new PilotCallResult.Failure("Choose an action from this research agent's conversation.");
                }
                long instanceId = token / MissionRules.ActionStride;
                var operation = (MissionAction)(token % MissionRules.ActionStride);
                if ((instanceId == 0) != (operation == MissionAction.Request))
                {
                    return new PilotCallResult.Failure("Use the action from the current research mission.");
                }
                var update = missions.Act(pilotId, agentId, operation, instanceId == 0 ? null : instanceId);
                Notify(context, agentId, update);
                if (update.Event == "completed")
                {
                    return Conversation(Current(), $"Mission complete. You earned {update.Offer!.ResearchPoints:0.00} RP.", update.Event);
                }
                if (operation != MissionAction.Defer && update.Offer is { State: MissionState.Offered or MissionState.Accepted })
                {
                    return new MissionConversation(update.Offer, update.Event, "Request a research mission from this agent.");
                }
                return Conversation(Current(), "Your research project continues.", update.Event);
        }
    }
}
