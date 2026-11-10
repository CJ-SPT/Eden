using Eden.Gameplay.Configuration;

namespace Eden.Server.Administration;

internal enum SettingsActivity
{
    Gathering,
    Rewards,
    Progression,
    Industry,
    World,
    Advanced,
}

internal sealed class SettingsWorkspace
{
    internal SettingsForm Form { get; private set; } = SettingsForm.From(new());
    internal WorldConfigurationDraft Draft { get; private set; } = new();
    private WorldConfigurationDraft Saved = new();
    internal SettingsActivity Activity { get; set; }
    internal WorldConfigurationDraft Current => Draft with { Settings = Form.Build() };
    internal IReadOnlyList<RevisionDifference> Changes => ConfigurationDisplay.Compare(Saved, Current);

    internal bool Unsaved
    {
        get
        {
            try
            {
                return Changes.Count != 0;
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException)
            {
                return true;
            }
        }
    }

    internal void Load(WorldConfigurationDraft draft)
    {
        Draft = draft;
        Form = SettingsForm.From(draft.Settings);
        // Normalize the legacy nullable market preference for form comparisons.
        Saved = Current;
    }

    internal void RestoreOriginal(WorldConfigurationDraft original)
    {
        Draft = original;
        Form = SettingsForm.From(original.Settings);
    }

    internal void ChangeOverrides(WorldConfigurationDraft draft) => Draft = draft;

    internal void Discard() => Load(Saved);
}
