using Eden.Gameplay.Characters;

namespace Eden.Gameplay.Alliances;

public sealed record AllianceContact(long ContactId, double Standing, long LabelMask);

public sealed record AllianceBulletin(long Id, string Title, string Body, long EditorId, long EditedAt, int Order);

public sealed record AllianceSocialBook(
    int AllianceId,
    long Revision,
    IReadOnlyList<AllianceContact> Contacts,
    IReadOnlyList<ContactLabel> Labels,
    IReadOnlyList<AllianceBulletin> Bulletins,
    string Motd,
    IReadOnlyList<AllianceCalendarEntry> Events
)
{
    public static AllianceSocialBook Empty(int allianceId) => new(allianceId, 0, [], [], [], "", []);
}

public interface IAllianceSocial
{
    AllianceSocialBook Read(long actorId);
    long Revision(int allianceId);
    double? Standing(int allianceId, long contactId);
    void SetStanding(long actorId, IReadOnlyList<long> contacts, double standing);
    void DeleteContacts(long actorId, IReadOnlyList<long> contacts);
    long CreateLabel(long actorId, string name, int? color);
    void EditLabel(long actorId, long labelId, string? name, int? color);
    void DeleteLabel(long actorId, long labelId);
    void SetLabels(long actorId, IReadOnlyList<long> contacts, long mask, bool assign);
    long SaveBulletin(long actorId, string title, string body, long? id, long? editedAt);
    void DeleteBulletin(long actorId, long id);
    void OrderBulletins(long actorId, IReadOnlyList<long> ids);
    string ReadMotd(int allianceId);
    void SetMotd(long actorId, int allianceId, string text);
}

public static class AllianceSocialRules
{
    // Eden limit. The pinned client passes 15 to GetBulletins, but does not establish the server's storage limit.
    public const int MaximumBulletins = 15;
}
