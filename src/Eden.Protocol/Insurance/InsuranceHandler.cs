using Eden.Gameplay.Insurance;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Insurance;

[Injectable]
internal sealed class InsuranceHandler(IPilotInsurance insurance) : ISelectedPilotCallHandler
{
    private const int MaximumPriceBatch = 4096;

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool standing =
            service == "corpStationMgr" && method == "DoStandingCheckForStationService" && args is [(long)StationService.Insurance];
        if (
            !standing
            && (
                service != "insuranceSvc"
                || method
                    is not (
                        "GetInsurancePrice"
                        or "GetInsurancePrices"
                        or "GetContracts"
                        or "GetContractForShip"
                        or "InsureShip"
                        or "UnInsureShip"
                    )
            )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 } && (method != "InsureShip" || keywords.Keys.Any(key => key != "voidOld")))
            {
                throw new InvalidDataException("Unexpected insurance arguments.");
            }
            var pilot = context.Pilot;
            if (standing)
            {
                insurance.CheckStation(pilot.PilotId, pilot.StationId);
                return PilotCallResult.None;
            }
            switch (method, args)
            {
                case ("GetInsurancePrice", [long type]) when type is > 0 and <= int.MaxValue:
                    return (double)insurance.Price((int)type) / MarketDefinitions.CentsPerIsk;
                case ("GetInsurancePrices", [List<object?> types])
                    when types.Count <= MaximumPriceBatch && types.All(type => type is long and > 0 and <= int.MaxValue):
                    return new InsurancePricesReply(insurance.Prices(types.Cast<long>().Select(type => (int)type).ToArray()));
                case ("GetContracts", [] or [false] or [0L]):
                    return new BlueList<InsuranceContractReply>(
                        insurance.Contracts(pilot.PilotId).Select(p => new InsuranceContractReply(p)).ToArray()
                    );
                // NPC-corporation pilots have no corporation-owned policies.
                case ("GetContracts", [true] or [1L]):
                    return BlueEmpty.List;
                case ("GetContractForShip", [long ship]):
                    return insurance.Contract(pilot.PilotId, ship) is { } contract
                        ? new InsuranceContractReply(contract)
                        : PilotCallResult.None;
                case ("InsureShip", [long ship, var premium, var corporation]):
                    if (Flag(corporation))
                    {
                        throw new InvalidDataException("Corporation ship insurance is not available yet.");
                    }
                    double amount = premium switch
                    {
                        double value => value,
                        long value => value,
                        _ => throw new InvalidDataException("Choose a valid insurance premium."),
                    };
                    var result = insurance.Insure(
                        pilot.PilotId,
                        pilot.StationId,
                        ship,
                        InsuranceRules.PaymentCents(amount),
                        keywords?.TryGetValue("voidOld", out var replace) == true && Flag(replace)
                    );
                    context.Notifications.Add(
                        new AccountBalanceChanged(pilot.PilotId, (double)result.BalanceCents / MarketDefinitions.CentsPerIsk)
                    );
                    return PilotCallResult.None;
                case ("UnInsureShip", [long ship]):
                    insurance.Cancel(pilot.PilotId, pilot.StationId, ship);
                    return PilotCallResult.None;
                default:
                    return new PilotCallResult.Failure("Invalid insurance request.");
            }
        }
        catch (InsuranceContractExists existing)
        {
            // The pinned quote window catches this error and confirms a second
            // InsureShip request with voidOld=True; do not silently replace it.
            return new PilotCallResult.NamedFailure("InsureShipFailedSingleContract", new InsuranceOwnerValues(existing.OwnerId));
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("This insurance amount is too large.");
        }
    }

    private static bool Flag(object? value) =>
        value switch
        {
            bool flag => flag,
            long flag when flag is 0 or 1 => flag != 0,
            _ => throw new InvalidDataException("Choose a valid insurance option."),
        };
}
