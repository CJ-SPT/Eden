using Eden.Gameplay.Characters;

namespace Eden.Gameplay.Corporations;

public sealed record CorporationContact(long ContactId, double Standing, long LabelMask);

public sealed record CorporationContactBook(
    int CorporationId,
    IReadOnlyList<CorporationContact> Contacts,
    IReadOnlyList<ContactLabel> Labels
);

public interface ICorporationContacts
{
    long Revision(int corporationId);
    CorporationContactBook Read(long actorId);
    void SetStanding(long actorId, IReadOnlyList<long> contacts, double standing);
    void Delete(long actorId, IReadOnlyList<long> contacts);
    long CreateLabel(long actorId, string name, int? color);
    void EditLabel(long actorId, long labelId, string? name, int? color);
    void DeleteLabel(long actorId, long labelId);
    void SetLabels(long actorId, IReadOnlyList<long> contacts, long mask, bool assign);
}
