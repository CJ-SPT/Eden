using System.Numerics;
using Eden.Gameplay.Market;

namespace Eden.Gameplay.Corporations;

public sealed record CorporationRecruitmentDraft(
    string Title,
    string Description,
    long TypeMask,
    long LanguageMask,
    long HourMask,
    long OtherMask,
    long MinimumSkillPoints,
    IReadOnlyList<long> Recruiters
);

public sealed record CorporationRecruitmentAd(
    long Id,
    int CorporationId,
    long CreatedAt,
    long ExpiresAt,
    CorporationRecruitmentDraft Details,
    int MemberCount
);

public sealed record CorporationRecruitmentSearch(
    long TypeMask,
    long LanguageMask,
    bool ExcludeAlliances,
    bool ExcludeFriendlyFire,
    long? SkillPoints,
    int MinimumMembers,
    int MaximumMembers,
    double MaximumTaxRate,
    double MaximumLoyaltyTaxRate,
    long HourMask,
    long OtherMask
);

public sealed record CorporationRecruitmentMatch(int Grade, CorporationRecruitmentAd Ad);

public interface ICorporationRecruitment
{
    long Create(long actorId, CorporationRecruitmentDraft draft, int days);
    void Update(long actorId, long id, CorporationRecruitmentDraft draft, int addedDays);
    void Delete(long actorId, long id);
    IReadOnlyList<CorporationRecruitmentAd> ForCorporation(int corporationId, bool includeExpired = false);
    IReadOnlyList<CorporationRecruitmentMatch> Search(CorporationRecruitmentSearch query);
    IReadOnlyList<long> Recruiters(long id);
}

public static class CorporationRecruitmentRules
{
    // Build 3662168 appConst and evecorporation.corp_ui_recruitment_const.
    public const int MaximumAds = 3;
    public const int MaximumDays = 28;
    public const int MaximumRecruiters = 6;
    public const long DailyFeeCents = 250_000 * MarketDefinitions.CentsPerIsk;
    public const long CreationFeeCents = 500_000 * MarketDefinitions.CentsPerIsk;
    public const long HoursMask = (1L << 24) - 1;
    public const long LanguagesMask = (1L << 1) | (1L << 7) | (1L << 8) | (1L << 9) | (1L << 10) | (1L << 11) | (1L << 42);
    public const long AreasMask = (1L << 12) | (1L << 37) | (1L << 38) | (1L << 39);
    public const long OtherOptionsMask = (1L << 0) | (1L << 23) | (1L << 34);
    public const long PveMask =
        (1L << 2) | (1L << 25) | (1L << 28) | (1L << 35) | (1L << 36) | (1L << 40) | (1L << 41) | (1L << 43) | (1L << 44);
    public const long PvpMask = (1L << 16) | (1L << 17) | (1L << 18) | (1L << 19) | (1L << 20) | (1L << 21) | (1L << 26) | (1L << 31);
    public const long IndustryMask = (1L << 13) | (1L << 22) | (1L << 24) | (1L << 29);
    public const long ActivitiesMask = PveMask | PvpMask | IndustryMask;

    public static long Fee(int days, bool creating)
    {
        if (days < (creating ? 1 : 0) || days > MaximumDays)
        {
            throw new InvalidDataException("Choose an advertisement duration of up to 28 days.");
        }
        return days * DailyFeeCents + (creating ? CreationFeeCents : 0);
    }

    public static void Validate(CorporationRecruitmentDraft draft)
    {
        if (
            string.IsNullOrWhiteSpace(draft.Title)
            || draft.Title.Length > 40
            || draft.Title.Any(char.IsControl)
            || draft.Description.Length > 1000
            || draft.Description.Contains('\0')
        )
        {
            throw new InvalidDataException("Recruitment ads require a title of up to 40 characters and a description of up to 1000.");
        }
        ValidateMasks(draft.TypeMask, draft.LanguageMask, draft.HourMask, draft.OtherMask);
        int activityGroups = new[] { PveMask, PvpMask, IndustryMask }.Count(mask => (draft.TypeMask & mask) != 0);
        if (
            Count(draft.TypeMask & ActivitiesMask) > 4
            || activityGroups > 1
            || Count(draft.TypeMask & AreasMask) > 2
            || Count(draft.HourMask) > 8
        )
        {
            throw new InvalidDataException("Choose up to four related activities, two operating areas and eight active hours.");
        }
        if (
            draft.MinimumSkillPoints < 0
            || draft.Recruiters.Count > MaximumRecruiters
            || draft.Recruiters.Any(id => id <= 0)
            || draft.Recruiters.Distinct().Count() != draft.Recruiters.Count
        )
        {
            throw new InvalidDataException("Choose up to six distinct recruiters and a nonnegative skill-point requirement.");
        }
    }

    public static void Validate(CorporationRecruitmentSearch query)
    {
        ValidateMasks(query.TypeMask, query.LanguageMask, query.HourMask, query.OtherMask);
        if (
            query.SkillPoints < 0
            || query.MinimumMembers < 0
            || query.MaximumMembers < query.MinimumMembers
            || !double.IsFinite(query.MaximumTaxRate)
            || query.MaximumTaxRate is < 0 or > 1
            || !double.IsFinite(query.MaximumLoyaltyTaxRate)
            || query.MaximumLoyaltyTaxRate is < 0 or > 1
        )
        {
            throw new InvalidDataException("Invalid recruitment search limits.");
        }
    }

    public static int? Match(CorporationRecruitmentSearch query, CorporationRecruitmentAd ad, CorporationDetails corporation)
    {
        var details = ad.Details;
        if (
            ad.MemberCount < query.MinimumMembers
            || ad.MemberCount > query.MaximumMembers
            || corporation.TaxRate > query.MaximumTaxRate
            || corporation.LoyaltyPointTaxRate > query.MaximumLoyaltyTaxRate
            || query.ExcludeFriendlyFire && corporation.FriendlyFireEnabled
            || query.SkillPoints is long points && details.MinimumSkillPoints > points
            || !Overlap(query.TypeMask & ActivitiesMask, details.TypeMask)
            || !Overlap(query.TypeMask & AreasMask, details.TypeMask)
            || !Overlap(query.LanguageMask, details.LanguageMask)
            || !Overlap(query.HourMask, details.HourMask)
            || (details.OtherMask & query.OtherMask) != query.OtherMask
        )
        {
            return null;
        }
        return Count(query.TypeMask & details.TypeMask)
            + Count(query.LanguageMask & details.LanguageMask)
            + Count(query.HourMask & details.HourMask)
            + Count(query.OtherMask & details.OtherMask);
    }

    private static bool Overlap(long requested, long offered) => requested == 0 || (requested & offered) != 0;

    private static int Count(long mask) => BitOperations.PopCount((ulong)mask);

    private static void ValidateMasks(long activities, long languages, long hours, long other)
    {
        if (
            (activities & ~(ActivitiesMask | AreasMask)) != 0
            || (languages & ~LanguagesMask) != 0
            || (hours & ~HoursMask) != 0
            || (other & ~OtherOptionsMask) != 0
        )
        {
            throw new InvalidDataException("Unknown recruitment options.");
        }
    }
}
