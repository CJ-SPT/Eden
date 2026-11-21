using Eden.Gameplay.Alliances;
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

    public static void CheckAllianceDonation(AllianceMember source, AllianceMember target, int executor, bool fromCorporation, long now)
    {
        if (
            source.AllianceId != target.AllianceId
            || (target.CorporationId != executor && (!fromCorporation || source.CorporationId != executor))
        )
        {
            throw new InvalidDataException("Choose your alliance executor, or send from its corporation wallet to a member corporation.");
        }
        if (
            AllianceMembershipRules.MembershipDays(source.JoinedAt, now) < EverMarkMembership.Days
            || AllianceMembershipRules.MembershipDays(target.JoinedAt, now) < EverMarkMembership.Days
        )
        {
            throw new InvalidDataException("Both corporations must have belonged to the alliance for 30 days.");
        }
    }

    public static void Check(long balance, long destination, long points)
    {
        if (points <= 0 || balance < points || destination < 0 || destination > long.MaxValue - points)
        {
            throw new InvalidDataException("Choose a positive LP amount within the available and destination balances.");
        }
    }
}
