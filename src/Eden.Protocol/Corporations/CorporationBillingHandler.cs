using System.Collections;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
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
                is not (
                    "GetAutomaticPaySettings"
                    or "SendAutomaticPaySettings"
                    or "GetCorporationBills"
                    or "GetCorporationBillsReceivable"
                    or "PayCorporationBill"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 } && (method != "PayCorporationBill" || keywords.Keys.Any(key => key != "fromAccountKey")))
            {
                throw new InvalidDataException("Unexpected billing arguments.");
            }
            switch (method, args)
            {
                case ("GetAutomaticPaySettings", []):
                    return new CorporationBillPaymentSettingsReply(billing.ReadAutomaticPayment(context.Pilot.PilotId));
                case ("GetCorporationBills", []):
                    return new BlueList<CorporationWarBillReply>(
                        billing.WarInvoices(context.Pilot.PilotId).Select(invoice => new CorporationWarBillReply(invoice)).ToArray()
                    );
                case ("GetCorporationBillsReceivable", []):
                    billing.Read(context.Pilot.PilotId);
                    return BlueEmpty.List;
                case ("PayCorporationBill", [long billId, ..]) when args.Length is 1 or 2:
                    object? account =
                        keywords?.GetValueOrDefault("fromAccountKey")
                        ?? (args.Length == 2 ? args[1] : (long)context.Pilot.Identity.CorporationAccountKey);
                    if (
                        account is not long accountKey
                        || accountKey is < CorporationWalletRules.MasterKey or > CorporationWalletRules.LastKey
                    )
                    {
                        throw new InvalidDataException("Choose a corporation wallet division.");
                    }
                    if (!billing.PayWarInvoice(context.Pilot.PilotId, billId, (int)accountKey))
                    {
                        throw new InvalidDataException("Choose an outstanding corporation war bill.");
                    }
                    return PilotCallResult.None;
                case ("SendAutomaticPaySettings", [IDictionary owners]) when owners.Count is 1 or 2:
                    SaveSettings(context, owners);
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

    private void SaveSettings(PilotCallContext context, IDictionary owners)
    {
        long corporation = context.Pilot.CorpId;
        if (!owners.Contains(corporation))
        {
            throw new InvalidDataException("Choose your corporation's payment settings.");
        }
        var settings = ReadSettings((int)corporation, owners[corporation]);
        AllianceMaintenancePaymentRule? maintenance = null;
        foreach (DictionaryEntry entry in owners)
        {
            if (Equals(entry.Key, corporation))
            {
                continue;
            }
            if (entry.Key is not long ownerId || ownerId is <= 0 or > int.MaxValue)
            {
                throw new InvalidDataException("Choose a valid automatic payment owner.");
            }
            var alliance = ReadSettings((int)ownerId, entry.Value);
            if (alliance.Rules is not [var rule] || rule.BillTypeId != AllianceBillingRules.MaintenanceBillType)
            {
                throw new InvalidDataException("Choose an alliance maintenance payment setting.");
            }
            maintenance = new((int)ownerId, rule.Enabled);
        }
        billing.Save(context.Pilot.PilotId, settings, maintenance);
    }

    private static CorporationBillPaymentSettings ReadSettings(int ownerId, object? settings)
    {
        int division = CorporationWalletRules.MasterKey;
        List<CorporationBillPaymentRule> rules = [];
        var fields = settings switch
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
        return new(ownerId, division, rules);
    }
}

internal sealed record CorporationWarBillReply(CorporationWarInvoice Invoice) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["billID"] = Invoice.Id,
                ["billTypeID"] = CorporationWarRules.WarBillType,
                ["amount"] = Invoice.AmountCents / (double)MarketDefinitions.CentsPerIsk,
                ["interest"] = 0d,
                ["debtorID"] = Invoice.DebtorId,
                ["creditorID"] = AllianceBillingRules.ConcordCorporationId,
                ["dueDateTime"] = Invoice.DueAt,
                ["paid"] = 0,
                ["paidDateTime"] = null,
                ["paidByOwnerID"] = 0,
                ["externalID"] = Invoice.DefenderId,
                ["externalID2"] = Invoice.WarId,
            }
        );
}

internal sealed record CorporationBillPaymentSettingsReply(CorporationBillPaymentSnapshot Snapshot) : BlueModel
{
    public override object ToBlue()
    {
        var settings = Snapshot.Corporation;
        List<(object? Key, object? Value)> fields = settings
            .Rules.Select(rule => ((object?)(long)rule.BillTypeId, (object?)rule.Enabled))
            .ToList();
        fields.Add(("divisionID", settings.DivisionKey));
        Dictionary<long, object?> owners = new() { [settings.CorporationId] = new BlueDictionary(fields) };
        if (Snapshot.MaintenanceAllianceId is int alliance)
        {
            owners.Add(
                alliance,
                new Dictionary<long, object?> { [AllianceBillingRules.MaintenanceBillType] = Snapshot.MaintenanceEnabled }
            );
        }
        return owners;
    }
}
