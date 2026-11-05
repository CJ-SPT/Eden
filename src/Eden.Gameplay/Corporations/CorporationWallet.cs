using Eden.Gameplay.Market;

namespace Eden.Gameplay.Corporations;

public sealed record CorporationWalletDivision(int Key, long BalanceCents);

public sealed record WalletTransferRequest(bool FromCorporation, int FromKey, long RecipientId, int ToKey, long AmountCents, string Reason);

public sealed record CorporationWalletEntry(
    long Id,
    long OwnerId,
    int Key,
    long Time,
    long CounterpartyId,
    long AmountCents,
    long BalanceCents,
    WalletEntryType EntryType,
    long ReferenceId,
    string Reason
);

public interface ICorporationWallet
{
    IReadOnlyList<CorporationWalletDivision> Divisions(long actorId);
    long Balance(long actorId, int key);
    void SelectDivision(long actorId, int key);
    void Transfer(long actorId, WalletTransferRequest request);
    IReadOnlyList<CorporationWalletEntry> Journal(long actorId, bool corporation, int key);
}

public static class CorporationWalletRules
{
    public const int MasterKey = 1000;
    public const int DivisionCount = 7;
    public const int LastKey = MasterKey + DivisionCount - 1;
    public const int MaximumReasonLength = 1000;
    public const long FirstJournalId = 17_000_000_000_000;

    // Build 3662168 appConst.minCorporationTaxAmount, expressed in cents.
    public const long MinimumTaxableBountyCents = 100_000 * MarketDefinitions.CentsPerIsk;
    public const WalletEntryType PlayerDonationEntry = WalletEntryType.PlayerDonation;
    public const WalletEntryType CorporationWithdrawalEntry = WalletEntryType.CorporationAccountWithdrawal;
    public const WalletEntryType BountyTaxEntry = WalletEntryType.BountyPrizeCorporationTax;

    public static void CheckKey(int key)
    {
        if (key is < MasterKey or > LastKey)
        {
            throw new InvalidDataException("Choose one of the seven corporation wallet divisions.");
        }
    }

    public static bool CanTake(CorporationRoleSet roles, int key)
    {
        CheckKey(key);
        return roles.Has(CorporationRole.Director) || (roles.Roles & ((long)CorporationRole.AccountCanTake1 << (key - MasterKey))) != 0;
    }

    public static bool CanRead(CorporationRoleSet roles, int key) =>
        roles.Has(CorporationRole.Accountant | CorporationRole.JuniorAccountant) || CanTake(roles, key);

    public static long Tax(long grossCents, double rate)
    {
        if (grossCents < 0 || !double.IsFinite(rate) || rate is < 0 or > 1)
        {
            throw new InvalidDataException("Invalid corporation tax inputs.");
        }
        return grossCents > MinimumTaxableBountyCents ? checked((long)decimal.Truncate(grossCents * (decimal)rate)) : 0;
    }

    public static void CheckTransfer(WalletTransferRequest request)
    {
        CheckKey(request.FromKey);
        CheckKey(request.ToKey);
        if (
            request.AmountCents <= 0
            || request.RecipientId <= 0
            || request.Reason.Length > MaximumReasonLength
            || request.Reason.Contains('\0')
        )
        {
            throw new InvalidDataException("Choose a positive amount, a known recipient and a reason of at most 1,000 characters.");
        }
    }
}
