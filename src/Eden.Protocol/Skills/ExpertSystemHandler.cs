using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Skills;

[Injectable]
internal sealed class ExpertSystemHandler(IPilotExpertSystems expertSystems, SkillHandler skills, TimeProvider clock)
    : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "expertSystemMgr" || method is not ("GetMyExpertSystems" or "ConsumeExpertSystem" or "RemoveMyExpertSystem"))
        {
            return PilotCallResult.Unsupported;
        }
        if (keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Choose a valid Expert System.");
        }

        try
        {
            Advance(context);
            long pilotId = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("GetMyExpertSystems", []):
                    var state = expertSystems.Read(pilotId);
                    context.ObserveExpertSystems(state);
                    return new ExpertSystemsReply(state.Systems);
                case ("ConsumeExpertSystem", [long itemId]) when itemId > 0:
                    Apply(context, expertSystems.Consume(pilotId, itemId));
                    return PilotCallResult.None;
                case ("RemoveMyExpertSystem", [long typeId]) when typeId is > 0 and <= int.MaxValue:
                    Apply(context, expertSystems.Remove(pilotId, (int)typeId));
                    return PilotCallResult.None;
                default:
                    return new PilotCallResult.Failure("Choose a valid Expert System.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    internal void Enter(PilotCallContext context) => Settle(context);

    internal void Advance(PilotCallContext context)
    {
        if (
            context.Selected
            && context.NextExpertSystemExpiry is long deadline
            && deadline <= clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()
        )
        {
            Settle(context);
        }
    }

    private void Settle(PilotCallContext context)
    {
        long pilotId = context.Pilot.PilotId;
        if (expertSystems.Expire(pilotId) is { } expired)
        {
            Apply(context, expired);
        }
        else
        {
            context.ObserveExpertSystems(expertSystems.Read(pilotId));
        }
    }

    private void Apply(PilotCallContext context, ExpertSystemChange change)
    {
        context.ObserveExpertSystems(change.After);
        if (change.Inventory is { } inventory)
        {
            InventoryChangesWire.NotifyOwnedItemChange(context, inventory);
        }
        if (change.Events.Count > 0)
        {
            var announcement = change.Events.FirstOrDefault(entry => entry.Added) ?? change.Events[0];
            context.Notifications.Add(new ExpertSystemsUpdated(new(change.After.Systems), announcement));
        }
        skills.ApplyExpertSystems(context, change);
    }
}
