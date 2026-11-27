using Eden.Gameplay.Alliances;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Alliances;

[Injectable]
internal sealed class AllianceBillingHandler(IAllianceBilling billing, ICorporationBilling corporationBilling) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "allianceRegistry" || method is not ("GetBills" or "GetBillsReceivable" or "GetBillBalance" or "PayBill"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (method != "PayBill" && keywords is { Count: > 0 } || keywords?.Keys.Any(key => key != "fromAccountKey") == true)
            {
                throw new InvalidDataException("Unexpected alliance billing arguments.");
            }
            long actor = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("GetBills", []):
                    return new BlueList<BlueModel>([
                        .. billing.Bills(actor).Where(b => b.PaidAt is null).Select(b => new AllianceBillReply(b)),
                        .. corporationBilling.WarInvoices(actor, alliance: true).Select(invoice => new CorporationWarBillReply(invoice)),
                    ]);
                case ("GetBillsReceivable", []):
                    _ = billing.Bills(actor);
                    return BlueEmpty.List;
                // The native alliance panel supplies its selected bill ID. The balance belongs to the active division.
                case ("GetBillBalance", []):
                case ("GetBillBalance", [long billId]) when billId > 0:
                    return billing.Balance(actor) / (double)MarketDefinitions.CentsPerIsk;
                case ("PayBill", [long id, ..]) when args.Length is 1 or 2 && (args.Length == 1 || args[1] is long):
                    object? requested =
                        keywords?.GetValueOrDefault("fromAccountKey")
                        ?? (args.Length == 2 ? args[1] : (long)context.Pilot.Identity.CorporationAccountKey);
                    if (requested is not long key || key is < CorporationWalletRules.MasterKey or > CorporationWalletRules.LastKey)
                    {
                        throw new InvalidDataException("Choose a corporation wallet division.");
                    }
                    if (!corporationBilling.PayWarInvoice(actor, id, (int)key, alliance: true))
                    {
                        billing.Pay(actor, id, (int)key);
                    }
                    return PilotCallResult.None;
                default:
                    throw new InvalidDataException("Invalid alliance billing request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record AllianceBillReply(AllianceBill Bill) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["billID"] = Bill.Id,
                ["billTypeID"] = AllianceBillingRules.MaintenanceBillType,
                ["amount"] = Bill.AmountCents / (double)MarketDefinitions.CentsPerIsk,
                ["interest"] = 0d,
                ["debtorID"] = Bill.AllianceId,
                ["creditorID"] = AllianceBillingRules.ConcordCorporationId,
                ["dueDateTime"] = Bill.DueAt,
                ["paid"] = Bill.PaidAt.HasValue,
                ["paidDateTime"] = Bill.PaidAt,
                ["paidByOwnerID"] = Bill.PaidByCorporationId,
                ["externalID"] = Bill.AllianceId,
                ["externalID2"] = -1,
            }
        );
}
