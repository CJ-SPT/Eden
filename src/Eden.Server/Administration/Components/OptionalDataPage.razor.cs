namespace Eden.Server.Administration.Components;

public partial class OptionalDataPage
{
    private readonly CancellationTokenSource _stopping = new();
    private Task? _refresh;
    private AdminOptionalData? Installed;
    private (OptionalDataImportStatus? Market, OptionalDataImportStatus? Traffic) Status;
    private bool SessionExpired;
    private bool Running => Status.Market?.Running == true || Status.Traffic?.Running == true;

    protected override Task OnInitializedAsync() => Load(initial: true);

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !SessionExpired)
        {
            _refresh = Refresh();
        }
        return Task.CompletedTask;
    }

    private async Task Load(bool initial = false)
    {
        try
        {
            var user = (await Authentication.GetAuthenticationStateAsync()).User;
            var previous = Status;
            Status = await Operations.OptionalDataStatus(user);
            if (initial || previous != Status && !Running)
            {
                Installed = await Operations.OptionalData(user);
            }
        }
        catch (UnauthorizedAccessException)
        {
            SessionExpired = true;
        }
    }

    private async Task Refresh()
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(3));
        try
        {
            while (!SessionExpired && await timer.WaitForNextTickAsync(_stopping.Token))
            {
                await InvokeAsync(async () =>
                {
                    var previous = Status;
                    await Load();
                    if (previous != Status || SessionExpired)
                    {
                        StateHasChanged();
                    }
                });
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Navigation ends polling while the server-owned import continues.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_refresh is not null)
        {
            await _refresh;
        }
        _stopping.Dispose();
        GC.SuppressFinalize(this);
    }
}
