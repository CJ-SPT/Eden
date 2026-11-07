using Microsoft.AspNetCore.Components;

namespace Eden.Server.Administration.Components;

public partial class PerformancePage
{
    private readonly CancellationTokenSource _stopping = new();
    private Task? _refresh;
    private ServerPerformanceSnapshot? Snapshot;
    private string? Error;

    protected override Task OnInitializedAsync() => Load();

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && Error is null)
        {
            _refresh = Refresh();
        }
        return Task.CompletedTask;
    }

    private async Task Load()
    {
        try
        {
            Snapshot = await Operations.Performance((await Authentication.GetAuthenticationStateAsync()).User);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException)
        {
            Snapshot = null;
            Error = "Server performance is unavailable. Check your administrator session and the server logs.";
        }
    }

    private async Task Refresh()
    {
        using PeriodicTimer timer = new(ServerPerformanceMonitor.SampleInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token))
            {
                await InvokeAsync(async () =>
                {
                    await Load();
                    StateHasChanged();
                });
                if (Error is not null)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Navigation or circuit disposal ends this page's refresh loop.
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
