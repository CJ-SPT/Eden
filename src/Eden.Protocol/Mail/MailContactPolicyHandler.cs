using Eden.Gameplay.Mail;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Mail;

[Injectable]
internal sealed class MailContactPolicyHandler(IMailContactPolicy policy) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "account" || method is not ("GetDefaultContactCost" or "SetContactCost"))
        {
            return PilotCallResult.Unsupported;
        }
        if (keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Unexpected mail contact settings arguments.");
        }
        long pilot = context.Pilot.PilotId;
        try
        {
            switch (method, args)
            {
                case ("GetDefaultContactCost", []):
                    return policy.ContactCost(pilot) is long savedCost ? savedCost : PilotCallResult.None;
                case ("SetContactCost", [null]):
                    policy.SetContactCost(pilot, null);
                    return PilotCallResult.None;
                case ("SetContactCost", [long cost]):
                    policy.SetContactCost(pilot, cost);
                    return PilotCallResult.None;
                default:
                    return new PilotCallResult.Failure("Invalid mail contact settings arguments.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
