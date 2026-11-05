using Eden.Gameplay.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

internal sealed partial class CorporationHandler
{
    private PilotCallResult CallAdministration(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords
    )
    {
        if (service != "corpRegistry" || method is not ("UpdateCorporation" or "UpdateLogo" or "UpdateDivisionNames"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected corporation administration arguments.");
            }
            long actor = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("UpdateCorporation", [var description, var url, var tax, var recruiting, var lpTax]):
                    administration.Update(actor, new(Text(description), Text(url), Rate(tax), Flag(recruiting), Rate(lpTax)));
                    break;
                case ("UpdateLogo", { Length: 7 }):
                    administration.UpdateLogo(
                        actor,
                        new(
                            OptionalId(args[0]),
                            OptionalId(args[1]),
                            OptionalId(args[2]),
                            OptionalId(args[3]),
                            OptionalId(args[4]),
                            OptionalId(args[5]),
                            OptionalId(args[6])
                        )
                    );
                    break;
                case ("UpdateDivisionNames", { Length: 14 }):
                    string? Name(int index) => args[index] is null ? null : Text(args[index]);
                    administration.UpdateDivisions(
                        actor,
                        new(
                            Name(0),
                            Name(1),
                            Name(2),
                            Name(3),
                            Name(4),
                            Name(5),
                            Name(6),
                            Name(7),
                            Name(8),
                            Name(9),
                            Name(10),
                            Name(11),
                            Name(12),
                            Name(13)
                        )
                    );
                    break;
                default:
                    throw new InvalidDataException("Invalid corporation administration request.");
            }
            Advance(context);
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record CorporationDetailsChanged(CorporationAdministrationChange Change) : PilotNotification("OnCorporationChanged")
{
    public override object?[] Arguments
    {
        get
        {
            var before = Fields(Change.Before);
            var after = Fields(Change.After);
            return
            [
                Change.CorporationId,
                after
                    .Where(p => !Equals(before[p.Key], p.Value))
                    .ToDictionary(p => p.Key, p => (object?)new object?[] { before[p.Key], p.Value }),
            ];
        }
    }

    private static Dictionary<string, object?> Fields(CorporationDetails details)
    {
        var logo = details.Logo;
        Dictionary<string, object?> fields = new()
        {
            ["description"] = new BlueUnicode(details.Description),
            ["url"] = details.Url,
            ["taxRate"] = details.TaxRate,
            ["loyaltyPointTaxRate"] = details.LoyaltyPointTaxRate,
            ["isRecruiting"] = details.ApplicationsEnabled,
            ["shape1"] = logo.Shape1,
            ["shape2"] = logo.Shape2,
            ["shape3"] = logo.Shape3,
            ["color1"] = logo.Color1,
            ["color2"] = logo.Color2,
            ["color3"] = logo.Color3,
            ["typeface"] = logo.Typeface,
        };
        var divisions = CorporationDivisionNames.Values(details.Divisions);
        foreach (var field in divisions)
        {
            fields.Add(field.Key, field.Value);
        }
        return fields;
    }
}

internal static class CorporationDivisionNames
{
    internal static Dictionary<string, object?> Values(CorporationDivisions? divisions)
    {
        static object? Name(string? name) => name is null ? null : new BlueUnicode(name);
        return new()
        {
            ["division1"] = Name(divisions?.Division1),
            ["division2"] = Name(divisions?.Division2),
            ["division3"] = Name(divisions?.Division3),
            ["division4"] = Name(divisions?.Division4),
            ["division5"] = Name(divisions?.Division5),
            ["division6"] = Name(divisions?.Division6),
            ["division7"] = Name(divisions?.Division7),
            ["walletDivision2"] = Name(divisions?.WalletDivision2),
            ["walletDivision3"] = Name(divisions?.WalletDivision3),
            ["walletDivision4"] = Name(divisions?.WalletDivision4),
            ["walletDivision5"] = Name(divisions?.WalletDivision5),
            ["walletDivision6"] = Name(divisions?.WalletDivision6),
            ["walletDivision7"] = Name(divisions?.WalletDivision7),
        };
    }
}
