using Eden.Gameplay.Market;

namespace Eden.Gameplay.Corporations;

// Build 3662168 appConst.medalStatus.
public enum CorporationMedalStatus
{
    Deleted = 1,
    Private = 2,
    Public = 3,
}

public sealed record CorporationMedalGraphic(int Part, string Graphic, int Color);

public sealed record CorporationMedalDesign(string Title, string Description, IReadOnlyList<CorporationMedalGraphic> Graphics);

public sealed record CorporationMedal(long Id, int CorporationId, long CreatorId, long CreatedAt, CorporationMedalDesign Design);

public sealed record CorporationMedalAward(
    long MedalId,
    long RecipientId,
    long IssuerId,
    long IssuedAt,
    string Reason,
    CorporationMedalStatus Status
);

public sealed record ReceivedCorporationMedal(CorporationMedal Medal, CorporationMedalAward Award);

public sealed record CorporationMedalDetails(CorporationMedal Medal, int RecipientCount);

public sealed class InsufficientMedalFunds(long requiredCents) : Exception("The selected corporation wallet cannot cover the medal fee.")
{
    public long RequiredCents { get; } = requiredCents;
}

public interface ICorporationMedals
{
    long? Create(long actorId, CorporationMedalDesign design, bool confirmed);
    bool Award(long actorId, long medalId, IReadOnlyList<long> recipients, string reason, bool confirmed);
    IReadOnlyList<CorporationMedalDetails> Catalogue(long actorId, int corporationId);
    CorporationMedalDetails? Details(long medalId);
    IReadOnlyList<CorporationMedalAward> Recipients(long viewerId, long medalId);
    IReadOnlyList<ReceivedCorporationMedal> Received(long viewerId, long recipientId);
    void SetStatus(long actorId, IReadOnlyDictionary<long, CorporationMedalStatus> changes);
}

public static class CorporationMedalRules
{
    public const long FeeCents = 5_000_000L * MarketDefinitions.CentsPerIsk;
    public const int MaximumRecipients = 100;
    public const int MaximumStatusChanges = 1000;

    // Build 3662168 appConst medal title and description limits. The reason popup caps text at 200.
    public static void Validate(CorporationMedalDesign design)
    {
        if (design.Title.Trim().Length is < 3 or > 100 || design.Description.Trim().Length is < 10 or > 1000)
        {
            throw new InvalidDataException("Choose a medal title of 3–100 characters and a description of 10–1,000 characters.");
        }
        if (
            design.Title.Any(char.IsControl)
            || design.Description.Contains('\0')
            || design.Graphics.Count is < 2 or > 32
            || !design.Graphics.Any(graphic => graphic.Part == 1)
            || !design.Graphics.Any(graphic => graphic.Part == 2)
            || design.Graphics.Any(graphic =>
                graphic.Part is not (1 or 2)
                || graphic.Graphic.Length is < 3 or > 64
                || graphic.Graphic.Count(c => c == '.') != 1
                || !graphic.Graphic.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
            )
        )
        {
            throw new InvalidDataException("Choose a valid medal and ribbon design.");
        }
    }

    public static void ValidateAward(IReadOnlyList<long> recipients, string reason)
    {
        if (
            recipients.Count is < 1 or > MaximumRecipients
            || recipients.Any(id => id <= 0)
            || recipients.Distinct().Count() != recipients.Count
            || string.IsNullOrWhiteSpace(reason)
            || reason.Length > 200
            || reason.Contains('\0')
        )
        {
            throw new InvalidDataException("Choose distinct medal recipients and a reason of up to 200 characters.");
        }
    }
}
