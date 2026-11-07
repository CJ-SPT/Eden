using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Gameplay.Configuration;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Eden.Server.Administration.Components;

public partial class RevisionsPage
{
    private ConfigurationState? State;
    private IReadOnlyList<ConfigurationRevisionInfo> Revisions = [];
    private IReadOnlyList<RevisionDifference> Differences = [];
    private long Left;
    private long Right;
    private int HistoryPage;
    private int DifferencePage;
    private string? Error;
    private string? Message;
    private string? ImportDocument;

    private async Task<ClaimsPrincipal> User() => (await Authentication.GetAuthenticationStateAsync()).User;

    protected override async Task OnInitializedAsync()
    {
        await Load();
        Left = State?.Active.Id ?? 1;
        Right = State?.Pending?.Id ?? Left;
        await Compare();
    }

    private async Task Load()
    {
        try
        {
            var user = await User();
            State = await Operations.Read(user);
            Revisions = await Operations.History(user, HistoryPage * 50);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Error = error.Message;
        }
    }

    private async Task Compare()
    {
        try
        {
            var user = await User();
            Differences = await Operations.Compare(user, Left, Right);
            DifferencePage = 0;
            Error = null;
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            Error = error.Message;
        }
    }

    private async Task Restore()
    {
        try
        {
            State = await Operations.Restore(await User(), new(State!.Generation, Right));
            Message = $"Revision {State.Pending!.Id} saved · pending restart.";
            await Load();
        }
        catch (Exception error)
            when (error is InvalidDataException or InvalidOperationException or JsonException or IOException or UnauthorizedAccessException)
        {
            Error = error.Message;
        }
    }

    private async Task SelectImport(InputFileChangeEventArgs args)
    {
        try
        {
            using var stream = args.File.OpenReadStream(8 * 1024 * 1024);
            using StreamReader reader = new(stream);
            ImportDocument = await reader.ReadToEndAsync();
            Error = null;
        }
        catch (IOException error)
        {
            Error = error.Message;
        }
    }

    private async Task Import()
    {
        try
        {
            State = await Operations.Import(await User(), State!.Generation, ImportDocument!);
            Message = $"Revision {State.Pending!.Id} imported · pending restart.";
            ImportDocument = null;
            await Load();
        }
        catch (Exception error)
            when (error is InvalidDataException or InvalidOperationException or JsonException or IOException or UnauthorizedAccessException)
        {
            Error = error.Message;
        }
    }

    private async Task NextHistory()
    {
        HistoryPage++;
        await Load();
    }

    private async Task PreviousHistory()
    {
        HistoryPage--;
        await Load();
    }
}
