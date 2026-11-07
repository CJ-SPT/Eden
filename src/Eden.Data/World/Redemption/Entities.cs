using Eden.Gameplay.Inventory;
using Eden.Gameplay.Redemption;
using Eden.Gameplay.Skills;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.World.Redemption;

public sealed class SavedRedeemToken
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public string Reference { get; set; } = "";
    public int TypeId { get; set; }
    public int Quantity { get; set; }
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public int? StationId { get; set; }
    public long CreatedAt { get; set; }
    public long? ExpiresAt { get; set; }
    public RedeemTokenState State { get; set; }
    public long? FinishedAt { get; set; }
    public long? PilotId { get; set; }
    public long? ItemId { get; set; }
    public long? ReversedAt { get; set; }

    public RedeemToken Snapshot() => new(Id, TypeId, Quantity, Label, Description, StationId, CreatedAt, ExpiresAt);
}
