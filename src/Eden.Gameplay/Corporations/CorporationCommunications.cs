using Eden.Gameplay.Mail;

namespace Eden.Gameplay.Corporations;

public sealed record CorporationBulletin(long Id, string Title, string Body, long EditorId, long EditedAt, int Order);

public sealed record CorporationWelcomeMail(string Body, long? EditorId = null, long? EditedAt = null);

public sealed record CorporationCommunications(
    IReadOnlyList<CorporationBulletin> Bulletins,
    CorporationWelcomeMail WelcomeMail,
    long NextBulletinId = 1,
    string Motd = ""
)
{
    public static CorporationCommunications Empty { get; } = new([], new(""));
}

public interface ICorporationCommunications
{
    CorporationCommunications Read(long memberId);
    long SaveBulletin(long actorId, string title, string body, long? id, long? editedAt);
    void DeleteBulletin(long actorId, long id);
    void OrderBulletins(long actorId, IReadOnlyList<long> ids);
    void SetWelcomeMail(long actorId, string body);
    string ReadMotd(int corporationId);
    void SetMotd(long actorId, int corporationId, string text);
}

public static class CorporationCommunicationRules
{
    // Build 3662168 base_corporation.MAX_NUM_BULLETINS and corp_ui_bulletins limits.
    public const int MaximumBulletins = 10;
    public const int MaximumTitle = 100;
    public const int MaximumBody = 4000;

    public static void CheckBulletin(string title, string body)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > MaximumTitle || title.Any(char.IsControl))
        {
            throw new InvalidDataException("Choose a bulletin title of 1 to 100 characters.");
        }
        if (body.Length > MaximumBody || body.Contains('\0'))
        {
            throw new InvalidDataException("The bulletin body must contain at most 4000 characters and no nulls.");
        }
    }

    public static void CheckWelcomeMail(string body) => MailRules.Body(body);
}
