using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class SecurityOfficeHandler(IPilotSecurityOffice office, SecurityStatusHandler security, WalletBalanceMonitor wallet)
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
        if (service != "crimewatch" || method != "ApplyTagsForSecStatus")
        {
            return PilotCallResult.Unsupported;
        }
        if (args is not [var target, var quoted] || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Choose a target and the current security status.");
        }
        try
        {
            var request = new SecurityTagRequest(BlueRead.Number(target), BlueRead.Number(quoted));
            security.Enter(context);
            foreach (var change in office.Exchange(context.Pilot.PilotId, request))
            {
                InventoryChangesWire.NotifyItemChange(context, change);
            }
            wallet.Notify(context);
            security.Advance(context);
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("The security tag processing fee is too large.");
        }
    }
}
