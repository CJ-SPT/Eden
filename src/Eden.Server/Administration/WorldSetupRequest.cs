using Eden.Gameplay.Configuration;
using Eden.World.Configuration;
using Eden.World.Storage;

namespace Eden.Server.Administration;

internal enum WorldProfile
{
    Established,
    Fresh,
}

internal sealed class WorldSetupRequest(WorldDatabase database, WorldSetupState state)
{
    private readonly TaskCompletionSource<WorldSetupApproval> _approved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private WorldSetupApproval? _pending;

    public Task<WorldSetupApproval> Approved => _approved.Task;
    public WorldConfigurationDraft? Previous { get; } =
        state.NewWorld ? null : new WorldConfigurationStore(database, TimeProvider.System).Read().Active.Draft;

    internal static WorldProfile ParseProfile(string value) =>
        value switch
        {
            "established" => WorldProfile.Established,
            "fresh" => WorldProfile.Fresh,
            _ => throw new InvalidDataException("Choose Established Universe or Fresh Universe."),
        };

    public WorldConfigurationDraft Draft(WorldProfile profile)
    {
        if (!File.Exists(database.CatalogPath))
        {
            throw new InvalidDataException("Install the Eden catalog in the server data directory before creating a world.");
        }
        bool fresh = profile == WorldProfile.Fresh;
        var previous = Previous;
        var draft = (previous ?? new()) with { CatalogFingerprint = database.Catalog.Identity.SourceFingerprint };
        if (previous is not null && draft.Settings.FreshUniverse == fresh)
        {
            return draft;
        }

        var settings = draft.Settings;
        return draft with
        {
            Settings = settings with
            {
                FreshUniverse = fresh,
                Economy = settings.Economy with
                {
                    ChargeServiceCosts = true,
                    ProgressiveMarkets = fresh ? true : null,
                    RegionalHubSupply = !fresh,
                    BeginnerMarketSupply = !fresh,
                    SimulatedMarketHistory = !fresh,
                    StandardMarket = false,
                },
                DevelopmentSupply = settings.DevelopmentSupply with { TestingMarket = false, TestingGrants = !fresh },
                Progression = fresh
                    ? settings.Progression with
                    {
                        GrantStartingEquipment = true,
                        GrantStartingSkills = true,
                    }
                    : settings.Progression,
            },
        };
    }

    public void Prepare(WorldProfile profile, string document, string author)
    {
        lock (_gate)
        {
            if (_pending is not null)
            {
                throw new InvalidOperationException("World setup has already been approved.");
            }
            var draft = string.IsNullOrWhiteSpace(document) ? Draft(profile) : WorldSetupStore.Parse(document);
            if (draft.Settings is null || draft.Settings.FreshUniverse != (profile == WorldProfile.Fresh))
            {
                throw new InvalidDataException("The configuration document must match the selected world profile.");
            }
            _pending = WorldSetupStore.Approve(database, state, draft, author);
        }
    }

    public Task BeginStartup()
    {
        lock (_gate)
        {
            _approved.TrySetResult(_pending ?? throw new InvalidOperationException("Approve world setup before starting Eden."));
        }
        return Task.CompletedTask;
    }
}

internal sealed record AdministrationSite(WorldSetupRequest? Setup);
