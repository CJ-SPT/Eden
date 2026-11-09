namespace Eden.Data.World.Market;

public sealed class SavedRegionalMarketStock
{
    public long OrderId { get; set; }
    public long AnchorCents { get; set; }
    public long RefreshedTicks { get; set; }
}
