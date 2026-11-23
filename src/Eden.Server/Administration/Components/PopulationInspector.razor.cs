using System.Globalization;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Market;
using Microsoft.AspNetCore.Components;

namespace Eden.Server.Administration.Components;

public partial class PopulationInspector
{
    [Parameter]
    public string? View { get; set; }

    [Parameter]
    public long? Id { get; set; }

    [Parameter]
    public int RefreshVersion { get; set; }

    private CapsuleerDirectory Directory;
    private CapsuleerDirectoryPage? Results;
    private CapsuleerPilotInspection? SelectedBot;
    private CapsuleerCorporationInspection? SelectedCorporation;
    private CapsuleerAllianceInspection? SelectedAlliance;
    private string Search = "";
    private string AppliedSearch = "";
    private string? Error;
    private (CapsuleerDirectory Directory, long? Id, int RefreshVersion)? LastRead;

    protected override async Task OnParametersSetAsync()
    {
        var directory = View?.ToLowerInvariant() switch
        {
            "corporations" => CapsuleerDirectory.Corporations,
            "alliances" => CapsuleerDirectory.Alliances,
            _ => CapsuleerDirectory.Bots,
        };
        if (LastRead == (directory, Id, RefreshVersion))
        {
            return;
        }
        if (Directory != directory)
        {
            Search = AppliedSearch = "";
            Results = null;
        }
        Directory = directory;
        LastRead = (directory, Id, RefreshVersion);
        if (Id is long id)
        {
            await LoadDetails(id);
            return;
        }
        await LoadPage(Results?.Page ?? 0);
    }

    private Task SearchDirectory()
    {
        AppliedSearch = Search.Trim();
        return LoadPage(0);
    }

    private Task LoadPage(int page) =>
        Read(async user =>
        {
            Results = await Operations.BrowsePopulation(user, Directory, AppliedSearch, page);
        });

    private Task LoadDetails(long id) =>
        Read(async user =>
        {
            SelectedBot = null;
            SelectedCorporation = null;
            SelectedAlliance = null;
            switch (Directory)
            {
                case CapsuleerDirectory.Bots:
                    SelectedBot = await Operations.InspectBot(user, id);
                    break;
                case CapsuleerDirectory.Corporations when id is > 0 and <= int.MaxValue:
                    SelectedCorporation = await Operations.InspectBotCorporation(user, (int)id);
                    break;
                case CapsuleerDirectory.Alliances when id is > 0 and <= int.MaxValue:
                    SelectedAlliance = await Operations.InspectBotAlliance(user, (int)id);
                    break;
            }
        });

    private async Task Read(Func<System.Security.Claims.ClaimsPrincipal, Task> read)
    {
        try
        {
            Error = null;
            await read((await Authentication.GetAuthenticationStateAsync()).User);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException)
        {
            Error = "Inspection is unavailable. Check your administrator session and the server logs.";
        }
    }

    private static string Href(CapsuleerDirectory directory, long? id = null)
    {
        string path = $"/population?view={directory.ToString().ToLowerInvariant()}";
        return id.HasValue ? $"{path}&id={id.Value.ToString(CultureInfo.InvariantCulture)}" : path;
    }

    private static string DisplayName(CapsuleerOrganizationLink organization) =>
        organization.Ticker.Length > 0 ? $"{organization.Name} [{organization.Ticker}]" : organization.Name;

    private string SystemName(int id) => Catalog.Systems.GetValueOrDefault(id)?.Name ?? $"System {id}";

    private string Location(long id) =>
        id is > 0 and <= int.MaxValue && Catalog.StationNames.TryGetValue((int)id, out string? name) ? name : $"Location {id}";

    private string TypeName(int id) => Catalog.Items.GetValueOrDefault(id)?.Name ?? $"Type {id}";

    private static string Isk(long cents) => $"{(decimal)cents / MarketDefinitions.CentsPerIsk:N2} ISK";

    private static string Utc(DateTimeOffset? date) => date?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—";

    private static string IndustryActivityName(IndustryActivity activity) =>
        activity switch
        {
            IndustryActivity.ResearchTime => "Time efficiency research",
            IndustryActivity.ResearchMaterial => "Material efficiency research",
            _ => activity.ToString(),
        };

    private static string Status(CapsuleerPilotInspection bot) =>
        bot.Suspended ? "Suspended"
        : bot.SimulationActive ? "Enabled"
        : "Paused · no players online";

    private static string SkillStatus(CapsuleerSkillInspection skill) =>
        skill.EndsAt is null ? "Paused"
        : skill.EndsAt <= DateTimeOffset.UtcNow ? "Awaiting settlement"
        : skill.StartsAt > DateTimeOffset.UtcNow ? "Queued"
        : "Training";

    private static string NextAction(CapsuleerPilotInspection bot)
    {
        if (bot.NextAction is not { } action || bot.ActiveTimeUntilNextAction is not { } delay)
        {
            return "None";
        }
        string remaining = delay == TimeSpan.Zero ? "Due" : $"{Math.Ceiling(delay.TotalSeconds):N0} active seconds remaining";
        return $"{(action == CapsuleerAction.GateTransfer ? "Gate transfer" : action.ToString())} · {remaining}";
    }
}
