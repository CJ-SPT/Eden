using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationRecruitmentHandler(ICorporationRecruitment recruitment) : ISelectedPilotCallHandler
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
            service != "corpRecProxy"
            || method
                is not (
                    "CreateRecruitmentAd"
                    or "UpdateRecruitmentAd"
                    or "DeleteRecruitmentAd"
                    or "GetRecruiters"
                    or "GetRecruitmentAd"
                    or "GetRecruitmentAdsByCorpID"
                    or "GetRecruitmentAdsForCorporation"
                    or "SearchCorpAds"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (
                keywords is { Count: > 0 }
                && (method != "UpdateRecruitmentAd" || keywords.Keys.Any(k => k is not ("minSP" or "otherMask")))
            )
            {
                throw new InvalidDataException("Unexpected recruitment arguments.");
            }
            long actor = context.Pilot.PilotId;
            switch (method, args)
            {
                case (
                    "CreateRecruitmentAd",
                    [long days, long types, long languages, var description, var recruiters, var title, long hours, long points, long other]
                ) when days is >= 1 and <= CorporationRecruitmentRules.MaximumDays:
                    return recruitment.Create(
                        actor,
                        Draft(title, description, types, languages, hours, points, other, recruiters),
                        (int)days
                    );
                case (
                    "UpdateRecruitmentAd",
                    [long id, long types, long languages, var description, var recruiters, var title, long days, long hours]
                ) when id > 0 && days is >= 0 and <= CorporationRecruitmentRules.MaximumDays:
                    recruitment.Update(
                        actor,
                        id,
                        Draft(
                            title,
                            description,
                            types,
                            languages,
                            hours,
                            Keyword(keywords, "minSP"),
                            Keyword(keywords, "otherMask"),
                            recruiters
                        ),
                        (int)days
                    );
                    return PilotCallResult.None;
                case ("DeleteRecruitmentAd", [long id]) when id > 0:
                    recruitment.Delete(actor, id);
                    return PilotCallResult.None;
                case ("GetRecruiters", [long id]) when id > 0:
                    return new BlueList<long>(recruitment.Recruiters(id));
                case ("GetRecruitmentAdsForCorporation", []):
                    return new CorporationRecruitmentRows(recruitment.ForCorporation(context.Pilot.CorpId, true));
                case ("GetRecruitmentAdsByCorpID", [long corporation]) when corporation is > 0 and <= int.MaxValue:
                    return new CorporationRecruitmentMatches(
                        recruitment.ForCorporation((int)corporation).Select(ad => new CorporationRecruitmentMatch(0, ad)).ToArray()
                    );
                case ("GetRecruitmentAd", [long corporation, long id]) when corporation is > 0 and <= int.MaxValue && id > 0:
                    var ad =
                        recruitment.ForCorporation((int)corporation).SingleOrDefault(ad => ad.Id == id)
                        ?? throw new InvalidDataException("This recruitment advertisement has expired or been removed.");
                    return new CorporationRecruitmentRow(ad);
                case (
                    "SearchCorpAds",
                    [
                        long types,
                        long languages,
                        bool alliances,
                        bool friendlyFire,
                        var points,
                        long minimum,
                        long maximum,
                        var tax,
                        var loyaltyTax,
                        long hours,
                        long other,
                    ]
                ) when minimum is >= 0 and <= int.MaxValue && maximum is >= 0 and <= int.MaxValue:
                    return new CorporationRecruitmentMatches(
                        recruitment.Search(
                            new(
                                types,
                                languages,
                                alliances,
                                friendlyFire,
                                points switch
                                {
                                    null => null,
                                    long value when value >= 0 => value,
                                    _ => throw new InvalidDataException("Invalid skill points."),
                                },
                                (int)minimum,
                                (int)maximum,
                                Rate(tax),
                                Rate(loyaltyTax),
                                hours,
                                other
                            )
                        )
                    );
                default:
                    throw new InvalidDataException("Invalid recruitment request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static CorporationRecruitmentDraft Draft(
        object? title,
        object? description,
        long types,
        long languages,
        long hours,
        long points,
        long other,
        object? recruiters
    ) =>
        new(
            Text(title),
            Text(description),
            types,
            languages,
            hours,
            other,
            points,
            recruiters is List<object?> ids && ids.Count <= CorporationRecruitmentRules.MaximumRecruiters && ids.All(id => id is long)
                ? ids.Cast<long>().ToArray()
                : throw new InvalidDataException("Invalid recruiter list.")
        );

    private static long Keyword(Dictionary<string, object?>? keywords, string key) =>
        keywords?.GetValueOrDefault(key) switch
        {
            null => 0,
            long value when value >= 0 => value,
            _ => throw new InvalidDataException("Invalid recruitment option."),
        };

    private static string Text(object? value) =>
        value is string or BlueUnicode or byte[] ? BlueMarshal.Text(value) : throw new InvalidDataException("Expected recruitment text.");

    private static double Rate(object? value) =>
        value switch
        {
            double number when double.IsFinite(number) && number is >= 0 and <= 1 => number,
            long number when number is >= 0 and <= 1 => number,
            _ => throw new InvalidDataException("Invalid tax limit."),
        };
}

internal sealed record CorporationRecruitmentRow(CorporationRecruitmentAd Ad) : BlueModel
{
    public override object ToBlue() =>
        new BlueConstructor(
            "blue.DBRow",
            [
                BlueRows.Descriptor([
                    ("adID", BlueColumnType.Int64),
                    ("corporationID", BlueColumnType.Int32),
                    ("allianceID", BlueColumnType.Int32),
                    ("channelID", BlueColumnType.Int64),
                    ("createDateTime", BlueColumnType.Int64),
                    ("expiryDateTime", BlueColumnType.Int64),
                    ("title", BlueColumnType.Unicode),
                    ("description", BlueColumnType.Unicode),
                    ("typeMask", BlueColumnType.Int64),
                    ("langMask", BlueColumnType.Int64),
                    ("hourMask1", BlueColumnType.Int64),
                    ("hourMask2", BlueColumnType.Int64),
                    ("minSP", BlueColumnType.Int64),
                    ("otherMask", BlueColumnType.Int64),
                    ("memberCount", BlueColumnType.Int32),
                ]),
                new object?[]
                {
                    Ad.Id,
                    Ad.CorporationId,
                    Ad.AllianceId,
                    null,
                    Ad.CreatedAt,
                    Ad.ExpiresAt,
                    Ad.Details.Title,
                    Ad.Details.Description,
                    Ad.Details.TypeMask,
                    Ad.Details.LanguageMask,
                    Ad.Details.HourMask,
                    0L,
                    Ad.Details.MinimumSkillPoints,
                    Ad.Details.OtherMask,
                    Ad.MemberCount,
                },
            ]
        );
}

internal sealed record CorporationRecruitmentRows(IReadOnlyList<CorporationRecruitmentAd> Ads) : BlueModel
{
    public override object ToBlue() => Ads.Select(ad => (object?)new CorporationRecruitmentRow(ad).ToBlue()).ToList();
}

internal sealed record CorporationRecruitmentMatches(IReadOnlyList<CorporationRecruitmentMatch> Matches) : BlueModel
{
    public override object ToBlue() =>
        Matches.Select(match => (object?)new object?[] { match.Grade, new CorporationRecruitmentRow(match.Ad).ToBlue() }).ToList();
}

internal sealed record CorporationRecruitmentChanged() : PilotNotification("OnCorporationRecruitmentAdChanged")
{
    public override object?[] Arguments => [];
}
