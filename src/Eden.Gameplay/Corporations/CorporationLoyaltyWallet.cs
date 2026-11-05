using Eden.Gameplay.Market;

namespace Eden.Gameplay.Corporations;

public sealed record CorporationLoyaltyTransfer(
    long Id,
    long SourceOwnerId,
    int RecipientCorporationId,
    int IssuerCorporationId,
    long Points,
    long SourceBefore,
    long SourceAfter,
    long RecipientBefore,
    long RecipientAfter,
    long Time
);

public interface ICorporationLoyaltyWallet
{
    IReadOnlyList<LoyaltyBalance> Read(long actorId);
    CorporationLoyaltyTransfer Transfer(long actorId, bool fromCorporation, int recipientId, int issuerId, long points);
}

public static class CorporationLoyaltyRules
{
    // Build 3662168 corporation.common.const pinned EM donation membership default.
    public static readonly TimeSpan EverMarkMembership = TimeSpan.FromDays(30);

    public static void Check(long balance, long destination, long points)
    {
        if (points <= 0 || balance < points || destination < 0 || destination > long.MaxValue - points)
        {
            throw new InvalidDataException("Choose a positive LP amount within the available and destination balances.");
        }
    }
}
