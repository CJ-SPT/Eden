using Eden.Gameplay.Docking;

namespace Eden.Protocol.PilotSessions;

[Injectable]
internal sealed class CharacterLogoffHandler(IPilotLookup pilots, PilotUpdateCoordinator updates) : IPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object? arguments,
        Dictionary<string, object?>? keywords
    )
    {
        if (service != "userSvc" || method != "UserLogOffCharacter")
        {
            return PilotCallResult.Unsupported;
        }
        if (arguments is not object?[] { Length: 0 } || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Character logoff does not accept arguments.");
        }
        if (!context.Selected)
        {
            return false;
        }
        updates.Leave(context);
        context.LogOff();
        context.Roster = pilots.ListPilots(context.AccountId);
        // GameUI.LogOffCharacter only opens character selection for a truthy reply.
        return true;
    }
}
