using System.Collections.Immutable;
using Eden.Gameplay.Configuration;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Economy;
using Eden.World.Storage;

namespace Eden.Server.Administration;

internal sealed class WorldSetupRequest(WorldDatabase database, WorldSetupState state)
{
    private readonly TaskCompletionSource<WorldSetupApproval> _approved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private WorldSetupApproval? _pending;

    public Task<WorldSetupApproval> Approved => _approved.Task;
    public WorldConfigurationDraft? Previous { get; } =
        state.NewWorld ? null : new WorldConfigurationStore(database, TimeProvider.System).Read().Active.Draft;

    internal static void RequireEstablished(string? profile)
    {
        if (profile is not (null or "" or "established"))
        {
            throw new InvalidDataException("World setup creates an Established Universe.");
        }
    }

    public WorldConfigurationDraft Draft()
    {
        if (!File.Exists(database.CatalogPath))
        {
            throw new InvalidDataException("Install the Eden catalog in the server data directory before creating a world.");
        }
        var draft = (Previous ?? new()) with { CatalogFingerprint = database.Catalog.Identity.SourceFingerprint };
        if (Previous is not null && !draft.Settings.FreshUniverse)
        {
            return draft;
        }

        using CatalogDbContext catalog = new(database.CatalogPath);
        var prices = EconomicStore.IndustryReferencePrices(catalog);
        foreach (var price in draft.AdjustedPrices)
        {
            prices[price.TypeId] = price.PriceIsk;
        }
        var settings = draft.Settings;
        return draft with
        {
            Settings = settings with
            {
                FreshUniverse = false,
                Economy = settings.Economy with
                {
                    ChargeServiceCosts = true,
                    ProgressiveMarkets = true,
                    RegionalHubSupply = true,
                    BeginnerMarketSupply = true,
                    SimulatedMarketHistory = true,
                    StandardMarket = false,
                },
                DevelopmentSupply = settings.DevelopmentSupply with { TestingMarket = false, TestingGrants = Previous is null },
                Capsuleers = settings.Capsuleers with { EarnedPvpShips = true },
            },
            AdjustedPrices = prices
                .OrderBy(price => price.Key)
                .Select(price => new AdjustedPriceOverride(price.Key, price.Value))
                .ToImmutableArray(),
        };
    }

    public void Prepare(string document, string author)
    {
        lock (_gate)
        {
            if (_pending is not null)
            {
                throw new InvalidOperationException("World setup has already been approved.");
            }
            var draft = string.IsNullOrWhiteSpace(document) ? Draft() : WorldSetupStore.Parse(document);
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
