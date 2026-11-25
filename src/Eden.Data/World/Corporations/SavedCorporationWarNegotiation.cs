using Eden.Gameplay.Corporations;

namespace Eden.Data.World.Corporations;

public sealed class SavedCorporationWarNegotiation
{
    public long Id { get; set; }
    public long WarId { get; set; }
    public CorporationWarNegotiationKind Kind { get; set; }
    public int FromOwnerId { get; set; }
    public int ToOwnerId { get; set; }
    public int AttackerId { get; set; }
    public int DefenderId { get; set; }
    public long AmountCents { get; set; }
    public string Description { get; set; } = "";
    public int? PayerCorporationId { get; set; }
    public int? PayerDivisionKey { get; set; }
    public CorporationWarNegotiationState State { get; set; }
    public long CreatedAt { get; set; }
    public long? AcceptedAt { get; set; }
    public long? DeclinedAt { get; set; }
    public long? RetractedAt { get; set; }
}

public sealed class SavedCorporationWarAlly
{
    public long WarId { get; set; }
    public int OwnerId { get; set; }
    public int AttackerId { get; set; }
    public long StartsAt { get; set; }
    public long EndsAt { get; set; }
}
