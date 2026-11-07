using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

[Injectable(InjectionType.Scoped)]
internal sealed class SecurityStatusHandler(IPilotSecurityStatus security, TimeProvider clock) : ISelectedPilotCallHandler
{
    private long? _pilot;
    private long _revision;
    private long? _due;
    private double _value;
    public bool IsMonitoring => _due.HasValue;

    public void Enter(PilotCallContext context)
    {
        if (_pilot == context.Pilot.PilotId)
        {
            return;
        }
        long revision = security.Revision;
        var state = security.Read(context.Pilot.PilotId);
        _pilot = context.Pilot.PilotId;
        _value = state.Value;
        _due = state.PendingAt;
        _revision = revision;
    }

    public void Reset()
    {
        _pilot = null;
        _due = null;
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            Reset();
            return;
        }
        Enter(context);
        if (_revision == security.Revision && (_due is null || clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() < _due))
        {
            return;
        }
        // Observe the revision before the snapshot. A kill committed between the
        // read and return must still trigger the next poll rather than disappear.
        long revision = security.Revision;
        var state = security.Read(context.Pilot.PilotId);
        if (state.Value != _value)
        {
            context.Notifications.Add(new SecurityStatusUpdated(state.Value));
        }
        _value = state.Value;
        _due = state.PendingAt;
        _revision = revision;
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            service != "crimewatch"
            || method is not ("GetMySecurityStatus" or "GetCharacterSecurityStatus" or "GetSecurityStatusTransactions")
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected security status arguments.");
            }
            return (method, args) switch
            {
                ("GetMySecurityStatus", []) => security.Read(context.Pilot.PilotId).Value,
                ("GetCharacterSecurityStatus", [long id]) when id > 0 => security.Read(id).Value,
                ("GetSecurityStatusTransactions", []) => new BlueList<SecurityTransactionRow>(
                    security.History(context.Pilot.PilotId).Select(t => new SecurityTransactionRow(t)).ToArray()
                ),
                _ => new PilotCallResult.Failure("Invalid security status request."),
            };
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record SecurityStatusUpdated(double Value) : PilotNotification("OnSecurityStatusUpdate")
{
    public override object?[] Arguments => [Value];
}

internal sealed record SecurityTransactionRow(SecurityStatusTransaction Transaction) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["eventID"] = Transaction.Id,
                ["eventDate"] = Transaction.Time,
                ["eventTypeID"] = (int)Transaction.Event,
                ["otherOwnerID"] = Transaction.OtherOwnerId,
                ["locationID"] = Transaction.LocationId,
                ["modification"] = Transaction.Modification,
                ["newValue"] = Transaction.NewValue,
            }
        );
}
