using System.Security.Claims;
using Eden.Gameplay.Skills;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;

namespace Eden.Server.Administration.Components;

public partial class SkillsPage
{
    private static readonly SkillEditorOption[] TrainingAttributes =
    [
        new(164, "Charisma"),
        new(165, "Intelligence"),
        new(166, "Memory"),
        new(167, "Perception"),
        new(168, "Willpower"),
    ];
    private SkillEditorSnapshot? Snapshot;
    private SkillEditorChoices? Options;
    private SkillPublicationStatus? Publication;
    private SkillEditorEntry? Selected;
    private SkillForm? Form;
    private EditContext? Context;
    private IReadOnlyList<SkillEditorRevision> History = [];
    private string ClientRoot = "";
    private string? Error;
    private string? Message;
    private bool Busy;
    private bool ExtraDirty;
    private bool Deleting;
    private Func<Task>? DiscardAction;
    private bool Dirty => ExtraDirty || Context?.IsModified() == true;
    private bool ShowAdvanced;
    private IReadOnlyList<SkillEditorOption> PrerequisiteOptions = [];

    private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    private async Task<ClaimsPrincipal> User() => (await Authentication.GetAuthenticationStateAsync()).User;

    protected override Task OnInitializedAsync() => Load();

    private async Task Load()
    {
        var user = await User();
        try
        {
            Snapshot = await Operations.Skills(user);
            Options ??= await Operations.SkillOptions(user);
            Publication = await Operations.SkillPublication(user);
            if (string.IsNullOrWhiteSpace(ClientRoot))
            {
                ClientRoot = Publication.ClientRoot ?? "";
            }
            Error = null;
        }
        catch (Exception error) when (Expected(error))
        {
            Error = error.Message;
        }
    }

    private static bool Expected(Exception error) =>
        error
            is InvalidDataException
                or InvalidOperationException
                or UnauthorizedAccessException
                or IOException
                or ArgumentException
                or OperationCanceledException;

    private async Task Act(Func<Task> action)
    {
        Busy = true;
        try
        {
            await action();
            Error = null;
        }
        catch (Exception error) when (Expected(error))
        {
            Error = error.Message;
            Message = null;
        }
        finally
        {
            Busy = false;
        }
    }

    private Task Schedule(Func<Task> action)
    {
        if (Dirty)
        {
            DiscardAction = action;
            return Task.CompletedTask;
        }
        return Act(action);
    }

    private async Task Discard()
    {
        var action = DiscardAction;
        DiscardAction = null;
        if (action is not null)
        {
            await Act(action);
        }
    }

    private void KeepDraft() => DiscardAction = null;

    private static string RaceName(int id) =>
        id switch
        {
            1 => "Caldari",
            2 => "Minmatar",
            4 => "Amarr",
            8 => "Gallente",
            _ => "Unknown race",
        };

    private Task Choose(SkillEditorEntry entry) => Schedule(() => Select(entry));

    private async Task Select(SkillEditorEntry entry)
    {
        Selected = entry;
        SetForm(entry.Definition);
        History = await Operations.SkillHistory(await User(), entry.Definition.TypeId);
    }

    private void SetForm(SkillDefinition skill)
    {
        Form = SkillForm.From(skill);
        Context = new(Form);
        ExtraDirty = false;
        Deleting = false;
        ShowAdvanced = false;
        PrerequisiteOptions = Snapshot!
            .Skills.Where(entry => !entry.Deleted && entry.Definition.TypeId != skill.TypeId)
            .Select(entry => new SkillEditorOption(entry.Definition.TypeId, entry.Definition.Name))
            .OrderBy(option => option.Name)
            .ToArray();
    }

    private Task New() =>
        Schedule(() =>
        {
            Selected = null;
            History = [];
            var template = Snapshot!.Skills.First().Definition;
            SetForm(
                new()
                {
                    GroupId = Options!.Groups.First().Id,
                    BasePrice = 10000,
                    MarketGroupId = template.MarketGroupId,
                    IconId = template.IconId,
                    AlphaLimits = [new(1, 0), new(2, 0), new(4, 0), new(8, 0)],
                }
            );
            return Task.CompletedTask;
        });

    private Task Duplicate() =>
        Schedule(() =>
        {
            var definition = Selected!.Definition;
            Selected = null;
            History = [];
            SetForm(definition with { TypeId = 0, Name = "Copy of " + definition.Name });
            return Task.CompletedTask;
        });

    private Task Reload() =>
        Schedule(async () =>
        {
            int? id = Selected?.Definition.TypeId;
            await Load();
            if (id is int selected && Snapshot!.Skills.FirstOrDefault(entry => entry.Definition.TypeId == selected) is { } entry)
            {
                await Select(entry);
            }
            else
            {
                Form = null;
                Context = null;
                ExtraDirty = false;
            }
        });

    private Task ResetDraft() =>
        Schedule(() =>
        {
            if (Selected is { } selected)
            {
                SetForm(selected.Definition);
            }
            else
            {
                Form = null;
                Context = null;
                ExtraDirty = false;
            }
            return Task.CompletedTask;
        });

    private Task Save() =>
        Act(async () =>
        {
            var saved = await Operations.SaveSkill(await User(), new(Snapshot!.Generation, Form!.Build()), Form.TypeId == 0);
            await Load();
            await Select(saved);
            Message = $"{saved.Definition.Name} saved. Build a package when you are ready to publish your changes.";
        });

    private Task RestoreOriginal() => Schedule(() => Restore(null));

    private Task RestoreRevision(long revision) => Schedule(() => Restore(revision));

    private async Task Restore(long? revision)
    {
        var saved = await Operations.RestoreSkill(await User(), Selected!.Definition.TypeId, new(Snapshot!.Generation, revision));
        await Load();
        await Select(saved);
        Message = "Skill values restored and saved. Publish a package to apply them to the game.";
    }

    private void ConfirmDelete() => Deleting = true;

    private void CancelDelete() => Deleting = false;

    private Task Delete() =>
        Act(async () =>
        {
            var deleted = await Operations.DeleteSkill(await User(), Selected!.Definition.TypeId, Snapshot!.Generation);
            await Load();
            await Select(deleted);
            Message = "Custom skill removed. Its history remains available for restoration.";
        });

    private Task Publish() =>
        Act(async () =>
        {
            var package = await Operations.PublishSkills(await User(), new(Snapshot!.Generation, ClientRoot));
            Publication = await Operations.SkillPublication(await User());
            Message = $"Revision {package.Generation} packaged. Restart the server; the launcher will install it before login.";
        });

    private Task BeforeNavigate(LocationChangingContext context)
    {
        if (Dirty)
        {
            context.PreventNavigation();
            Error = "Save or discard your skill draft before leaving this page.";
        }
        return Task.CompletedTask;
    }

    private void AddPrerequisite()
    {
        Form!.Prerequisites.Add(new());
        ExtraDirty = true;
    }

    private void RemovePrerequisite(SkillForm.PrerequisiteInput value)
    {
        Form!.Prerequisites.Remove(value);
        ExtraDirty = true;
    }

    private void AddAttribute()
    {
        Form!.Attributes.Add(new());
        ExtraDirty = true;
    }

    private void RemoveAttribute(SkillForm.AttributeInput value)
    {
        Form!.Attributes.Remove(value);
        ExtraDirty = true;
    }

    private void AddEffect()
    {
        Form!.Effects.Add(new());
        ExtraDirty = true;
    }

    private void RemoveEffect(SkillForm.EffectInput value)
    {
        Form!.Effects.Remove(value);
        ExtraDirty = true;
    }
}
