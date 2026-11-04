using Eden.Gameplay.Market;

namespace Eden.Gameplay.Redemption;

public enum RedeemCurrencyType
{
    // Build 3662168 inventorycommon.const typeKredits / typeLoyaltyPointsHeraldry.
    Isk = 52996,
    EverMarks = 73277,
}

public enum RedeemCurrencyKind
{
    Isk,
    EverMarks,
}

public sealed record RedeemedCurrency(long TokenId, long PilotId, RedeemCurrencyKind Kind, long Amount, long Before, long After, long Time);

public static class CurrencyRewardRules
{
    // Pinned appConst.corpHeraldry. EverMarks share the native LP wallet surface.
    public const int ParagonCorporation = 1000419;

    // Eden's journal namespace, following personal contracts at 12e12.
    public const long FirstJournalId = 13_000_000_000_000;

    public static RedeemCurrencyKind? Kind(int typeId) =>
        (RedeemCurrencyType)typeId switch
        {
            RedeemCurrencyType.Isk => RedeemCurrencyKind.Isk,
            RedeemCurrencyType.EverMarks => RedeemCurrencyKind.EverMarks,
            _ => null,
        };

    public static long Amount(RedeemCurrencyKind kind, int quantity)
    {
        if (quantity <= 0 || !Enum.IsDefined(kind))
        {
            throw new InvalidDataException("Invalid currency reward quantity or kind.");
        }
        return kind == RedeemCurrencyKind.Isk ? checked((long)quantity * MarketDefinitions.CentsPerIsk) : quantity;
    }

    public static long Credit(long balance, long amount)
    {
        if (balance < 0 || amount <= 0 || amount > long.MaxValue - balance)
        {
            throw new InvalidDataException("The currency reward would exceed the wallet limit.");
        }
        return balance + amount;
    }
}
