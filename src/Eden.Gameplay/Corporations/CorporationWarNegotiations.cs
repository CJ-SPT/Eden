namespace Eden.Gameplay.Corporations;

public enum CorporationWarNegotiationKind
{
    AllyOffer = 0,
    Surrender = 2,
}

public enum CorporationWarNegotiationState
{
    Pending = 0,
    Accepted = 1,
    Declined = 2,
    Retracted = 3,
}

public enum CorporationWarNegotiationAction
{
    Accept,
    Decline,
    Retract,
}

public enum CorporationWarPeaceReason
{
    HeadquartersLost = 1,
    Surrender = 4,
}

public sealed record CorporationWarNegotiation(
    long Id,
    long WarId,
    CorporationWarNegotiationKind Kind,
    int FromOwnerId,
    int ToOwnerId,
    int AttackerId,
    int DefenderId,
    long AmountCents,
    string Description,
    CorporationWarNegotiationState State,
    long CreatedAt,
    long? AcceptedAt,
    long? DeclinedAt,
    long? RetractedAt
);

public sealed record CorporationWarAlly(int OwnerId, long StartsAt, long EndsAt);
