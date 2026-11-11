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
        PilotCallResult Conversation(string message) => new ResearchConversation(Current(), message);
        void Changed(string name) => context.Notifications.Add(new ResearchChanged(name));
        if (action is null)
        {
            var current = Current();
            return new ResearchConversation(
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
                                return Conversation("Research selection cancelled.");
                            }
                            _ = research.StartResearch(pilotId, agentId, fields[index].SkillId);
                            Changed("research_started");
                            return Conversation("Research has started.");
                        }
                    )
                );
            case (long)ResearchAction.Cancel:
                var cancelled = Current() ?? throw new InvalidDataException("This agent has no active research project.");
                return new PilotCallResult.ClientCall(
                    new ResearchDialog(
                        "YesNo",
                        ["Cancel Research", "Cancel this project? All unspent research points with this agent will be lost.", agentId],
                        [],
                        answer =>
                        {
                            if (answer is not true)
                            {
                                return Conversation("Research continues.");
                            }
                            research.CancelResearch(pilotId, agentId, cancelled.Id);
                            Changed("research_update_ppd");
                            return Conversation("Research cancelled. Unspent research points have been discarded.");
                        }
                    )
                );
            case (long)ResearchAction.Buy:
                var project = Current() ?? throw new InvalidDataException("Start research before buying datacores.");
                int maximum = (int)Math.Min(int.MaxValue, Math.Floor(project.Points / ResearchRules.PointsPerDatacore));
                if (maximum == 0)
                {
                    return Conversation("You need at least 100 research points to buy a datacore.");
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
                                return Conversation("Datacore purchase cancelled.");
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
                            return Conversation("Datacores delivered to your Item Hangar.");
                        }
                    )
                );
            default:
                return new PilotCallResult.Failure("Choose an action from this research agent's conversation.");
        }
    }
}
