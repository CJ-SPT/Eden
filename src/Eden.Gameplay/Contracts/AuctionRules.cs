using Eden.Gameplay.Market;

namespace Eden.Gameplay.Contracts;

public sealed record AuctionTerms(long BuyoutCents);

public sealed record AuctionState(long BuyoutCents, long CurrentBidCents, bool IssuerCollected, bool WinnerCollected);

public sealed record ContractBid(long Id, long BidderId, long AmountCents, long Time);

public static class AuctionRules
{
    // Build 3662168 ContractsSvc.PlaceBid: ten percent of the starting bid,
    // truncated to whole ISK, with a 1,000 ISK floor and a buyout cap.
    public const long MinimumIncrementCents = 1000 * MarketDefinitions.CentsPerIsk;
    public const long MinimumPublicStartingCents = 1_000_000 * MarketDefinitions.CentsPerIsk;
    public const int MaximumDurationDays = 14;
    private const long IncrementDivisor = 10;

    // CCP documents repeated late-bid extensions without publishing the server's
    // exact window. Eden uses a 15-minute response window for its NPC-station auctions.
    public static readonly TimeSpan ExtensionWindow = TimeSpan.FromMinutes(15);

    public static long ExpiryAfterBid(long now, long expires) => Math.Max(expires, checked(now + ExtensionWindow.Ticks));

    public static void Check(ContractDraft draft)
    {
        if (draft.Type != ContractType.Auction)
        {
            if (draft.Auction is not null)
            {
                throw new InvalidDataException("Auction terms require an auction contract.");
            }
            return;
        }
        if (
            draft.Auction is not { } terms
            || terms.BuyoutCents < 0
            || terms.BuyoutCents > ContractRules.MaximumAmountCents
            || (terms.BuyoutCents > 0 && terms.BuyoutCents < draft.PriceCents)
            || draft.RewardCents != 0
            || draft.Items.Count == 0
            || draft.RequestedItems.Count != 0
            || draft.Courier is not null
            || (draft.AssigneeId is null && draft.PriceCents < MinimumPublicStartingCents)
            || draft.ExpiryMinutes > TimeSpan.FromDays(MaximumDurationDays).TotalMinutes
        )
        {
            throw new InvalidDataException("Public auctions start at 1 million ISK; choose goods and up to 14 days.");
        }
    }

    public static long MinimumBid(long starting, long current, long buyout)
    {
        long increment = Math.Max(
            MinimumIncrementCents,
            starting / (IncrementDivisor * MarketDefinitions.CentsPerIsk) * MarketDefinitions.CentsPerIsk
        );
        long next = checked(current + increment);
        if (buyout > 0)
        {
            next = Math.Min(next, buyout);
        }
        // The dialog truncates its computed minimum, but its buyout checkbox
        // sends the exact collateral value, including the creation form's cents.
        return Math.Max(starting, next) / MarketDefinitions.CentsPerIsk * MarketDefinitions.CentsPerIsk;
    }

    public static void CheckBid(long starting, long current, long buyout, long amount)
    {
        if (
            amount < MinimumBid(starting, current, buyout)
            || amount > ContractRules.MaximumAmountCents
            || amount <= current
            || (amount % MarketDefinitions.CentsPerIsk != 0 && amount != buyout)
            || (buyout > 0 && amount > buyout)
        )
        {
            throw new InvalidDataException("Bid at least the next minimum in whole ISK or take the exact buyout.");
        }
    }
}
