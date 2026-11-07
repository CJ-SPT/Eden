using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable(InjectionType.Scoped)]
internal sealed partial class CorporationHandler(
    IPlayerCorporations corporations,
    ICorporationMembership membership,
    ICorporationAdministration administration,
    ICorporationRoles roles,
    ICorporationShares shares
) : ISelectedPilotCallHandler
{
    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        DrainChanges(context);
        var identity = corporations.Identity(context.Pilot.PilotId);
        if (
            context.Pilot.CorpId == identity.Id
            && context.Pilot.Identity.CorporationRoles == identity.Roles
            && context.Pilot.Identity.CorporationAccess == identity.Access
            && context.Pilot.Identity.CorporationAccountKey == identity.AccountKey
        )
        {
            return;
        }
        if (context.Pilot.CorpId != identity.Id)
        {
            context.ReleaseCorporationReferences();
        }
        context.Flight.UpdateCorporation(identity);
        context.Roster = context.Roster.Select(p => p.PilotId == context.Pilot.PilotId ? context.Pilot : p).ToArray();
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        var shareResult = CallShares(context, service, method, args, keywords);
        if (shareResult is not PilotCallResult.UnsupportedCall)
        {
            return shareResult;
        }
        var adminResult = CallAdministration(context, service, method, args, keywords);
        if (adminResult is not PilotCallResult.UnsupportedCall)
        {
            return adminResult;
        }
        var roleResult = CallRoles(context, service, method, args, keywords);
        if (roleResult is not PilotCallResult.UnsupportedCall)
        {
            return roleResult;
        }
        var memberResult = CallMembership(context, service, method, args, keywords);
        if (memberResult is not PilotCallResult.UnsupportedCall)
        {
            return memberResult;
        }
        if (
            service != "corpRegistry"
            || method
                is not (
                    "AddCorporation"
                    or "GetSuggestedTickerNames"
                    or "GetEveOwners"
                    or "GetMember"
                    or "DoesMyCorpAcceptStructures"
                    or "DoesCorpRestrictCorpMails"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected corporation arguments.");
            }
            switch (method, args)
            {
                case ("AddCorporation", { Length: 15 }):
                    var details = new CorporationDetails(
                        Text(args[0]),
                        Text(args[1]),
                        Text(args[2]),
                        Text(args[3]),
                        Rate(args[4]),
                        Rate(args[14]),
                        new(
                            OptionalId(args[5]),
                            OptionalId(args[6]),
                            OptionalId(args[7]),
                            OptionalId(args[8]),
                            OptionalId(args[9]),
                            OptionalId(args[10]),
                            OptionalId(args[11])
                        ),
                        Flag(args[12]),
                        Flag(args[13])
                    );
                    var result = corporations.Create(context.Pilot.PilotId, details);
                    Advance(context);
                    context.Notifications.Add(
                        new AccountBalanceChanged(context.Pilot.PilotId, (double)result.BalanceCents / MarketDefinitions.CentsPerIsk)
                    );
                    return PilotCallResult.None;
                case ("GetSuggestedTickerNames", [var name]):
                    return new CorporationTickerSuggestions(corporations.SuggestTickers(Text(name)));
                case ("GetEveOwners", []):
                    return new CompatibilityRowset(
                        ["ownerID", "ownerName", "typeID"],
                        corporations.Members(context.Pilot.PilotId).Select(m => new EveOwnerRow(m.PilotId, m.Name)).ToArray()
                    );
                case ("GetMember", [long pilot]):
                    var member =
                        corporations.Members(context.Pilot.PilotId).SingleOrDefault(m => m.PilotId == pilot)
                        ?? throw new InvalidDataException("That pilot is not a member of your corporation.");
                    return new PlayerCorporationMemberReply(member);
                case ("DoesMyCorpAcceptStructures" or "DoesCorpRestrictCorpMails", []):
                    return false;
                default:
                    throw new InvalidDataException("Invalid corporation request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static string Text(object? value) =>
        value is string or byte[] ? BlueMarshal.Text(value) : throw new InvalidDataException("Expected corporation text.");

    private static double Rate(object? value) =>
        value switch
        {
            double number => number,
            long number => number,
            _ => throw new InvalidDataException("Expected a corporation tax rate."),
        };

    private static int? OptionalId(object? value) =>
        value switch
        {
            null => null,
            long id when id is >= 0 and <= int.MaxValue => (int)id,
            _ => throw new InvalidDataException("Invalid corporation logo value."),
        };

    private static bool Flag(object? value) =>
        value switch
        {
            bool flag => flag,
            0L => false,
            1L => true,
            _ => throw new InvalidDataException("Invalid corporation option."),
        };
}

internal sealed record CorporationTickerSuggestions(IReadOnlyList<string> Tickers) : BlueModel
{
    public override object ToBlue() =>
        new CompatibilityRowset(["tickerName"], Tickers.Select(t => new SuggestedTicker(t)).ToArray()).ToBlue();
}

internal sealed record SuggestedTicker(string Ticker) : BlueModel
{
    public override object ToBlue() => new object?[] { Ticker };
}

internal sealed record PlayerCorporationMemberReply(CorporationMember Member) : BlueModel
{
    public override object ToBlue()
    {
        var fields = CorporationRoleFields.Values(Member.Access ?? new(new(Member.Roles)));
        fields.Add("characterID", Member.PilotId);
        fields.Add("corporationID", Member.CorporationId);
        fields.Add("startDateTime", Member.JoinedAt);
        return new BlueInstance("utillib.KeyVal", fields);
    }
}

internal sealed record PlayerCorporationReply(PlayerCorporation Corporation) : BlueModel
{
    internal static BlueConstructor Descriptor() =>
        BlueRows.Descriptor([
            ("corporationID", BlueColumnType.Int32),
            ("corporationName", BlueColumnType.Unicode),
            ("tickerName", BlueColumnType.String),
            ("stationID", BlueColumnType.Int32),
            ("solarSystemID", BlueColumnType.Int32),
            ("factionID", BlueColumnType.Int32),
            ("ceoID", BlueColumnType.Int64),
            ("taxRate", BlueColumnType.Float64),
            ("shares", BlueColumnType.Int64),
            ("memberLimit", BlueColumnType.Int64),
            ("url", BlueColumnType.String),
            ("creatorID", BlueColumnType.Int64),
            ("memberCount", BlueColumnType.Int32),
            ("description", BlueColumnType.Unicode),
            ("loyaltyPointTaxRate", BlueColumnType.Float64),
            ("isRecruiting", BlueColumnType.Boolean),
            ("allianceID", BlueColumnType.Int32),
            ("shape1", BlueColumnType.Int32),
            ("shape2", BlueColumnType.Int32),
            ("shape3", BlueColumnType.Int32),
            ("color1", BlueColumnType.Int32),
            ("color2", BlueColumnType.Int32),
            ("color3", BlueColumnType.Int32),
            ("typeface", BlueColumnType.Int32),
            ("allowWar", BlueColumnType.Boolean),
            // GetDivisionNames reads these fields while constructing CEO role panels.
            // None selects the pinned client's localized default division names.
            ("division1", BlueColumnType.Unicode),
            ("division2", BlueColumnType.Unicode),
            ("division3", BlueColumnType.Unicode),
            ("division4", BlueColumnType.Unicode),
            ("division5", BlueColumnType.Unicode),
            ("division6", BlueColumnType.Unicode),
            ("division7", BlueColumnType.Unicode),
            ("walletDivision2", BlueColumnType.Unicode),
            ("walletDivision3", BlueColumnType.Unicode),
            ("walletDivision4", BlueColumnType.Unicode),
            ("walletDivision5", BlueColumnType.Unicode),
            ("walletDivision6", BlueColumnType.Unicode),
            ("walletDivision7", BlueColumnType.Unicode),
        ]);

    public override object ToBlue()
    {
        var details = Corporation.Details;
        var logo = details.Logo;
        var divisions = CorporationDivisionNames.Values(details.Divisions);
        return new BlueConstructor(
            "blue.DBRow",
            [
                Descriptor(),
                new object?[]
                {
                    Corporation.Id,
                    new BlueUnicode(details.Name),
                    details.Ticker,
                    Corporation.StationId,
                    Corporation.SystemId,
                    null,
                    Corporation.CeoId,
                    details.TaxRate,
                    CorporationRules.InitialShares,
                    (long)Corporation.MemberLimit,
                    details.Url,
                    Corporation.CreatorId,
                    Corporation.MemberCount,
                    new BlueUnicode(details.Description),
                    details.LoyaltyPointTaxRate,
                    details.ApplicationsEnabled,
                    null,
                    logo.Shape1,
                    logo.Shape2,
                    logo.Shape3,
                    logo.Color1,
                    logo.Color2,
                    logo.Color3,
                    logo.Typeface,
                    false,
                    divisions["division1"],
                    divisions["division2"],
                    divisions["division3"],
                    divisions["division4"],
                    divisions["division5"],
                    divisions["division6"],
                    divisions["division7"],
                    divisions["walletDivision2"],
                    divisions["walletDivision3"],
                    divisions["walletDivision4"],
                    divisions["walletDivision5"],
                    divisions["walletDivision6"],
                    divisions["walletDivision7"],
                },
            ]
        );
    }
}
