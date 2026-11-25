using Eden.Gameplay.Inventory;

namespace Eden.Data.World.Inventory;

public sealed class SavedPlexVaultEntry
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public long PilotId { get; set; }
    public PlexVaultTransferKind Kind { get; set; }
    public long Amount { get; set; }
    public long Balance { get; set; }
    public long Time { get; set; }
    public long? CounterpartyPilotId { get; set; }
}
