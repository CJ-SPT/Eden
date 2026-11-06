namespace Eden.Gameplay.Corporations;

public sealed record CorporationShareHolding(int CorporationId, long ShareholderId, long Shares, int? ShareholderCorporationId);

public sealed record CorporationShareTransfer(int CorporationId, bool FromCorporation, long RecipientId, long Shares);

public sealed record CorporationShareChange(int CorporationId, long ShareholderId, long Before, long After);

public sealed record CorporationShareBalances(long Source, long Destination);

public static class CorporationShareRules
{
    public static CorporationShareBalances Transfer(long source, long destination, long quantity)
    {
        if (source < 0 || destination < 0 || quantity <= 0 || quantity > source || destination > long.MaxValue - quantity)
        {
            throw new InvalidDataException("Choose a positive share amount within the source and recipient limits.");
        }
        return new(source - quantity, destination + quantity);
    }
}

public interface ICorporationShares
{
    IReadOnlyList<CorporationShareHolding> Holdings(long actorId, bool corporation);
    IReadOnlyList<CorporationShareHolding> Shareholders(long actorId, int corporationId);
    void Transfer(long actorId, CorporationShareTransfer request);
    bool CanObserve(long actorId, long shareholderId);
}
