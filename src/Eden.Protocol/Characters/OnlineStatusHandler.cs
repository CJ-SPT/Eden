using Eden.Gameplay.Characters;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

[Injectable(InjectionType.Scoped)]
internal sealed class OnlineStatusHandler(IPilotContacts contacts, SharedStationGuests presence) : ISelectedPilotCallHandler
{
    private Dictionary<long, bool>? _observed;

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "onlineStatus" || method is not ("GetInitialState" or "GetOnlineStatus"))
        {
            return PilotCallResult.Unsupported;
        }
        if (keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Unexpected online status arguments.");
        }
        if (method == "GetInitialState" && args.Length == 0)
        {
            _observed = Read(context);
            return new CompatibilityRowset(
                ["contactID", "online"],
                _observed.Select(entry => (IBlueWritable)new OnlineContactReply(entry.Key, entry.Value)).ToArray()
            );
        }
        if (method == "GetOnlineStatus" && args is [long id] && id > 0)
        {
            var state = contacts.Read(context.Pilot.PilotId);
            return !state.Blocked.Contains(id) && state.Addresses.Any(c => c.ContactId == id) && presence.IsOnline(id);
        }
        return new PilotCallResult.Failure("Invalid online status request.");
    }

    public void Advance(PilotCallContext context)
    {
        if (_observed is null)
        {
            return;
        }
        var current = Read(context);
        foreach (var entry in current)
        {
            if (_observed.TryGetValue(entry.Key, out bool previous) && previous != entry.Value)
            {
                context.Notifications.Add(new ContactPresenceChanged(entry.Key, entry.Value));
            }
        }
        _observed = current;
    }

    public void Reset() => _observed = null;

    private Dictionary<long, bool> Read(PilotCallContext context)
    {
        var state = contacts.Read(context.Pilot.PilotId);
        return state
            .Addresses.Where(c => c.InWatchlist)
            .ToDictionary(c => c.ContactId, c => !state.Blocked.Contains(c.ContactId) && presence.IsOnline(c.ContactId));
    }
}

internal sealed record OnlineContactReply(long ContactId, bool Online) : BlueModel
{
    public override object ToBlue() => new object?[] { ContactId, Online };
}

internal sealed record ContactPresenceChanged(long ContactId, bool Online)
    : PilotNotification(Online ? "OnContactLoggedOn" : "OnContactLoggedOff")
{
    public override object?[] Arguments => [ContactId];
}
