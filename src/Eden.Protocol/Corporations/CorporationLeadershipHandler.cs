using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationLeadershipHandler(ICorporationLeadership leadership, CorporationHandler corporations)
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
        if (service != "corpRegistry" || method is not ("PayoutDividend" or "GetNumberOfPotentialCEOs" or "ResignFromCEO"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected corporation leadership arguments.");
            }
            long actor = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("PayoutDividend", [var mode, var amount]):
                    bool shareholders = mode switch
                    {
                        bool value => value,
                        long value when value is 0 or 1 => value != 0,
                        _ => throw new InvalidDataException("Choose members or shareholders for the dividend."),
                    };
                    leadership.PayDividend(actor, shareholders, CorporationWalletHandler.Cents(amount));
                    return PilotCallResult.None;
                case ("GetNumberOfPotentialCEOs", []):
                    return new BlueList<long>(leadership.Successors(actor));
                case ("ResignFromCEO", [null]):
                    var successors = leadership.Successors(actor);
                    if (successors.Count == 0)
                    {
                        leadership.Close(actor);
                        corporations.Advance(context);
                        return PilotCallResult.None;
                    }
                    return new BlueList<long>(successors);
                case ("ResignFromCEO", [long successor]) when successor > 0:
                    leadership.Transfer(actor, successor);
                    corporations.Advance(context);
                    return PilotCallResult.None;
                default:
                    throw new InvalidDataException("Invalid corporation leadership request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
