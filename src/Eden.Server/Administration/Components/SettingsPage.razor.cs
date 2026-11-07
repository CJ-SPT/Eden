using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Gameplay.Configuration;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Eden.Server.Administration.Components;

public partial class SettingsPage
{
    private ConfigurationState? State;
    private WorldConfigurationDraft Original = new();
    private WorldConfigurationDraft Draft = new();
    private SettingsForm Form = new();
    private string? Error;
    private string? Message;
    private bool Busy;

    private async Task<ClaimsPrincipal> User() => (await Authentication.GetAuthenticationStateAsync()).User;

    protected override Task OnInitializedAsync() => Load();

    private async Task Load()
    {
        try
        {
            var user = await User();
            State = await Operations.Read(user);
            Original = (await Operations.Revision(user, 1)).Draft;
            Draft = State.Pending?.Draft ?? State.Active.Draft;
            Form = SettingsForm.From(Draft.Settings);
            Error = null;
        }
        catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException)
        {
            Error = error.Message;
        }
    }

    private void Reset()
    {
        Draft = Original;
        Form = SettingsForm.From(Original.Settings);
        Message = "Original values are in the draft. Save to stage them for restart.";
    }

    private void ChangeOverrides(WorldConfigurationDraft draft) => Draft = draft;

    private async Task Validate() => await Act(save: false);

    private async Task Save() => await Act(save: true);

    private async Task Act(bool save)
    {
        Busy = true;
        try
        {
            Draft = Draft with { Settings = Form.Build() };
            var user = await User();
            var validation = await Operations.Validate(user, Draft);
            if (!validation.Valid)
            {
                throw new InvalidDataException(string.Join(" ", validation.Messages));
            }
            if (save)
            {
                State = await Operations.Save(user, new(State!.Generation, Draft));
            }
            Message = save ? $"Revision {State!.Pending!.Id} saved · pending restart." : "Draft validation passed.";
            Error = null;
        }
        catch (Exception error)
            when (error is InvalidDataException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            Error = error.Message;
            Message = null;
        }
        finally
        {
            Busy = false;
        }
    }
}
