using Eden.Gameplay.Corporations;

namespace Eden.Data.World.Corporations;

public sealed class SavedCorporationWar
{
    public long Id { get; set; }
    public int AttackerId { get; set; }
    public int DefenderId { get; set; }
    public long? HeadquartersId { get; set; }
    public long DeclaredAt { get; set; }
    public long StartsAt { get; set; }
    public long? EndingAt { get; set; }
    public long? EndsAt { get; set; }
    public long? RetractedAt { get; set; }
    public int? RetractedBy { get; set; }
    public long? BillId { get; set; }
    public long? BillIssuedAt { get; set; }
    public long? BillDueAt { get; set; }
    public bool Mutual { get; set; }
    public CorporationWarEndReason? ReasonEnded { get; set; }
    public long? InheritedFromWarId { get; set; }
    public long? InheritedAt { get; set; }
}

public sealed class SavedCorporationWarInvitation
{
    public int FromOwnerId { get; set; }
    public int ToOwnerId { get; set; }
    public long SentAt { get; set; }
}

public sealed class SavedCorporationWarInvitationSettings
{
    public int OwnerId { get; set; }
}
