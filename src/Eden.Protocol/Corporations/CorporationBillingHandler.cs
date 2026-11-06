using System.Collections;
using Eden.Gameplay.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationBillingHandler(ICorporationBilling billing) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            service != "billMgr"
            || method
                is not ("GetAutomaticPaySettings" or "SendAutomaticPaySettings" or "GetCorporationBills" or "GetCorporationBillsReceivable")
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected billing arguments.");
            }
            switch (method, args)
            {
                case ("GetAutomaticPaySettings", []):
                    return new CorporationBillPaymentSettingsReply(billing.Read(context.Pilot.PilotId));
                case ("GetCorporationBills" or "GetCorporationBillsReceivable", []):
                    billing.Read(context.Pilot.PilotId);
                    // No published Eden service currently issues corporation invoices.
                    // Wallet transfers and taxes are settled immediately, not unpaid bills.
                    return BlueEmpty.List;
                case ("SendAutomaticPaySettings", [IDictionary owners]) when owners.Count == 1:
                    var owner = owners.Keys.Cast<object>().Single();
                    if (owner is not long corporation || corporation != context.Pilot.CorpId)
                    {
                        throw new InvalidDataException("Choose your corporation's payment settings.");
                    }
                    int division = CorporationWalletRules.MasterKey;
                    List<CorporationBillPaymentRule> rules = [];
                    var fields = owners[owner] switch
                    {
                        BlueDictionary mixed => mixed.Entries,
                        IDictionary uniform => uniform.Keys.Cast<object>().Select(key => ((object?)key, uniform[key])).ToArray(),
                        _ => throw new InvalidDataException("Invalid automatic payment settings."),
                    };
                    foreach (var field in fields)
                    {
                        if (
                            field.Key is string or byte[]
                            && BlueMarshal.Text(field.Key) == "divisionID"
                            && field.Value is long key
                            && key is >= CorporationWalletRules.MasterKey and <= CorporationWalletRules.LastKey
                        )
                        {
                            division = (int)key;
                        }
                        else if (field.Key is long type && type is > 0 and <= int.MaxValue && field.Value is bool enabled)
                        {
                            rules.Add(new((int)type, enabled));
                        }
                        else
                        {
                            throw new InvalidDataException("Invalid automatic payment setting.");
                        }
                    }
                    billing.Save(context.Pilot.PilotId, new((int)corporation, division, rules));
                    return PilotCallResult.None;
                default:
                    throw new InvalidDataException("Invalid corporation billing request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record CorporationBillPaymentSettingsReply(CorporationBillPaymentSettings Settings) : BlueModel
{
    public override object ToBlue()
    {
        List<(object? Key, object? Value)> fields = Settings.Rules.Select(r => ((object?)(long)r.BillTypeId, (object?)r.Enabled)).ToList();
        fields.Add(("divisionID", Settings.DivisionKey));
        return new Dictionary<long, object?> { [Settings.CorporationId] = new BlueDictionary(fields) };
    }
}
