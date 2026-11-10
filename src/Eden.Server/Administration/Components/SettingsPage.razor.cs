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
    private readonly SettingsWorkspace Workspace = new();
    private SettingsForm Form => Workspace.Form;
    private EditContext? Context;
    private bool Unsaved => Workspace.Unsaved || Context?.GetValidationMessages().Any() == true;
    private IReadOnlyList<RevisionDifference>? DraftChanges
    {
        get
        {
            try
            {
                return Context?.GetValidationMessages().Any() == true ? null : Workspace.Changes;
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException)
            {
                return null;
            }
        }
    }
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
            Workspace.Load(State.Pending?.Draft ?? State.Active.Draft);
            SetContext();
            Message = null;
            Error = null;
        }
        catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException)
        {
            Error = error.Message;
        }
    }

    private void Reset()
    {
        Workspace.RestoreOriginal(Original);
        SetContext();
        Error = null;
        Message = "Original values are in the draft. Save to stage them for restart.";
    }

    private void Discard()
    {
        Workspace.Discard();
        SetContext();
        Error = null;
        Message = "Unsaved changes discarded.";
    }

    private void SetContext()
    {
        if (Context is not null)
        {
            Context.OnValidationStateChanged -= ValidationChanged;
        }
        Context = new(Form);
        Context.OnValidationStateChanged += ValidationChanged;
    }

    private void ValidationChanged(object? sender, ValidationStateChangedEventArgs args) => StateHasChanged();

    private void Invalid() => Error = "Review the validation messages before saving. Changes in other tabs are retained.";

    private static string ActivityLabel(SettingsActivity activity) =>
        activity == SettingsActivity.World ? "World & economy" : activity.ToString();

    private async Task Validate() => await Act(save: false);

    private async Task Save() => await Act(save: true);

    private async Task Act(bool save)
    {
        Busy = true;
        try
        {
            if (!Context!.Validate())
            {
                Invalid();
                return;
            }
            var draft = Workspace.Current;
            var user = await User();
            var validation = await Operations.Validate(user, draft);
            if (!validation.Valid)
            {
                throw new InvalidDataException(string.Join(" ", validation.Messages));
            }
            if (save)
            {
                State = await Operations.Save(user, new(State!.Generation, draft));
                Workspace.Load(State.Pending!.Draft);
                SetContext();
            }
            Message = save ? $"Revision {State!.Pending!.Id} saved · pending restart." : "Draft validation passed.";
            Error = null;
        }
        catch (Exception error)
            when (error
                    is InvalidDataException
                        or InvalidOperationException
                        or UnauthorizedAccessException
                        or IOException
                        or OverflowException
            )
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
