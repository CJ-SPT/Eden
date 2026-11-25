namespace Eden.Gameplay.Inventory;

public interface IPlexVault
{
    long Balance(long accountId);
    IReadOnlyList<PlexVaultEntry> History(long accountId);
    PlexVaultEntry? HistoryEntry(long accountId, long id);
    InventoryChange Deposit(long pilotId, long? locationId, long itemId, int quantity);
    InventoryChange Withdraw(long pilotId, long locationId, int flagId, int quantity, bool forMarket);
}

public enum PlexVaultTransferKind
{
    Deposit = 1,
    InventoryWithdrawal = 2,
    MarketWithdrawal = 3,
}

public sealed record PlexVaultEntry(long Id, long PilotId, PlexVaultTransferKind Kind, int Amount, long Balance, long Time);

public enum PlexVaultFailure
{
    Account,
    Amount,
    Funds,
    Location,
}

public sealed class PlexVaultRejected(PlexVaultFailure reason) : Exception
{
    public PlexVaultFailure Reason { get; } = reason;
}

public static class PlexVaultRules
{
    // Build 3662168 eve_public/plex/plex.proto uses hundredths of a PLEX on the gateway.
    public const int CentsPerPlex = 100;
    public const long MaximumBalance = long.MaxValue / CentsPerPlex;
    public const int HistoryLimit = 2000;
}
