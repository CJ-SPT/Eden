using Eden.Gameplay.Capsuleers;

namespace Eden.Server.Administration.Components;

public partial class PopulationPage
{
    private const int PageSize = 50;
    private AdminPopulationSnapshot? Snapshot;
    private string? Error;
    private string Search = "";
    private string Reason = "";
    private int Page;
    private IReadOnlyList<CapsuleerSystemSummary> Filtered =>
        Snapshot
            ?.Systems.Where(s =>
                (Reason.Length == 0 || s.Eligibility.ToString() == Reason)
                && (
                    Search.Length == 0
                    || Name(s.SystemId).Contains(Search, StringComparison.OrdinalIgnoreCase)
                    || s.SystemId.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(Search, StringComparison.Ordinal)
                )
            )
            .ToArray()
        ?? [];

    private string Name(int system) => Catalog.Systems.GetValueOrDefault(system)?.Name ?? "Unknown system";

    protected override Task OnInitializedAsync() => Load();

    private void ResetPage() => Page = 0;

    private void Previous() => Page--;

    private void Next() => Page++;

    private async Task Load()
    {
        try
        {
            Snapshot = await Operations.Population((await Authentication.GetAuthenticationStateAsync()).User);
            Error = null;
            ResetPage();
        }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException)
        {
            Snapshot = null;
            Error = "Population information is unavailable. Check your administrator session and the server logs.";
        }
    }
}
