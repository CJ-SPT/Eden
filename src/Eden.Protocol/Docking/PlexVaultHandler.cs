using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class PlexVaultHandler(IPlexVault vault, TimeProvider clock) : ISelectedPilotCallHandler
{
    // Build 3662168 vgs.common.listeners registers the listener after the RPC returns.
    private static readonly TimeSpan s_listenerDelay = TimeSpan.FromMilliseconds(150);

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "invbroker" || method is not ("DepositPlexToVault" or "WithdrawPlexFromVault"))
        {
            return PilotCallResult.Unsupported;
        }
        if (keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Unexpected PLEX vault arguments.");
        }
        // vgs.common.const.InvoiceType values and the market/ordinary inventory call forms.
        bool deposit = method == "DepositPlexToVault";
        string reference = Guid.NewGuid().ToString("D");
        long pilot = context.Pilot.PilotId;
        try
        {
            var change = (deposit, args) switch
            {
                (true, [null or long, long item, long amount, var invoice]) when BlueRead.Text(invoice) == "InventoryToVault" =>
                    vault.Deposit(pilot, args[0] as long?, item, Quantity(amount)),
                (false, [long amount, long destination, var invoice, long flag, null]) when BlueRead.Text(invoice) == "VaultToInventory" =>
                    vault.Withdraw(pilot, destination, checked((int)flag), Quantity(amount), forMarket: false),
                (false, [long amount, long destination, var invoice, long flag]) when BlueRead.Text(invoice) == "VaultToMarket" =>
                    vault.Withdraw(pilot, destination, checked((int)flag), Quantity(amount), forMarket: true),
                _ => throw new PlexVaultRejected(PlexVaultFailure.Location),
            };
            InventoryChangesWire.NotifyItemChange(context, change);
            Schedule(new PlexVaultTransferCompleted(pilot, deposit, change.Item.Id, reference));
        }
        catch (PlexVaultRejected rejection)
        {
            Schedule(new PlexVaultTransferFailed(pilot, deposit, Error(deposit, rejection.Reason), reference));
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            Schedule(new PlexVaultTransferFailed(pilot, deposit, Error(deposit, PlexVaultFailure.Location), reference));
        }
        return reference;

        void Schedule(PilotNotification notice) => context.ScheduleNotification(clock.GetUtcNow() + s_listenerDelay, notice);
    }

    private static int Quantity(long amount) =>
        amount is > 0 and <= int.MaxValue ? (int)amount : throw new PlexVaultRejected(PlexVaultFailure.Amount);

    private static string Error(bool deposit, PlexVaultFailure reason) =>
        (deposit, reason) switch
        {
            (true, PlexVaultFailure.Account) => "PlexDepositAccountNotFound",
            (true, PlexVaultFailure.Amount) => "PlexDepositInvalidAmount",
            (true, _) => "PlexDepositInternalError",
            (false, PlexVaultFailure.Account) => "PlexWithdrawalAccountNotFound",
            (false, PlexVaultFailure.Amount) => "PlexWithdrawalInvalidAmount",
            (false, PlexVaultFailure.Funds) => "PlexWithdrawalInsufficientFunds",
            _ => "PlexWithdrawalInternalError",
        };
}

internal sealed record PlexVaultTransferCompleted(long PilotId, bool Deposit, long ItemId, string Reference)
    : PilotNotification(Deposit ? "OnPlexDeposited" : "OnPlexItemAddedToInventory", PilotId)
{
    public override object?[] Arguments => Deposit ? [Reference] : [ItemId, Reference];
}

internal sealed record PlexVaultTransferFailed(long PilotId, bool Deposit, string Error, string Reference)
    : PilotNotification(Deposit ? "OnPlexDepositFailed" : "OnPlexWithdrawFailed", PilotId)
{
    public override object?[] Arguments => [Error, Reference];
}
