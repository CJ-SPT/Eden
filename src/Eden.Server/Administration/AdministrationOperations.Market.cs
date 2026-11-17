namespace Eden.Server.Administration;

public sealed record AdminMarketSnapshot(DateTimeOffset? CapturedAt, int Orders, int Regions, int Excluded, string? Error);

internal sealed partial class AdministrationOperations
{
    internal async Task<AdminMarketSnapshot> MarketSnapshot(System.Security.Claims.ClaimsPrincipal user)
    {
        await Authorize(user);
        try
        {
            var manifest = database.MarketSnapshot.ReadManifest();
            return new(manifest.CapturedAt, manifest.ImportedOrders, manifest.Regions.Length, manifest.ExcludedOrders.Values.Sum(), null);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return new(null, 0, 0, 0, "No compatible live order snapshot is installed.");
        }
    }
}
