using System.Security.Claims;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Skills;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Skills;
using Microsoft.AspNetCore.Authorization;

namespace Eden.Server.Administration;

internal sealed record AdminSaveRequest(long Generation, WorldConfigurationDraft Draft);

internal sealed record AdminRestoreRequest(long Generation, long RevisionId);

internal sealed record AdminImportRequest(long Generation, string Document);

internal sealed record AdminValidationResult(bool Valid, IReadOnlyList<string> Messages);

internal sealed record AdminPopulationSnapshot(
    CapsuleerPopulationSummary Population,
    CapsuleerRuntimeSummary Runtime,
    int MaterializedPilots,
    IReadOnlyList<CapsuleerSystemSummary> Systems,
    CapsuleerPopulationPolicy Policy
)
{
    public IReadOnlyList<RegionalConflictRegion> RegionalConflicts { get; init; } = [];
}

internal sealed record AdminRecordView(CatalogStoredRecord Record, IReadOnlyList<CatalogRelatedRecord> Relationships);

internal sealed partial class AdministrationOperations(
    AdministratorAccounts administrators,
    IAuthorizationService authorization,
    IWorldConfiguration configuration,
    CatalogBrowser browser,
    CatalogProvider catalog,
    ServerPerformanceMonitor performance,
    ICapsuleerPopulation population,
    ICapsuleerDiagnostics capsuleers,
    ICapsuleerInspection inspection,
    ICapsuleerScene scene,
    Eden.World.Modding.ModStore mods,
    Eden.Server.Modding.ModPublisher modPublisher,
    Eden.World.Storage.WorldDatabase database,
    CapsuleerCorporationIdentityStore corporationIdentities,
    OptionalDataImports optionalDataImports,
    IServerKillboard killboard,
    IRegionalConflictDiagnostics? regionalConflicts = null
)
{
    internal IReadOnlyDictionary<int, string> RegionNames => catalog.Regions;
    internal IReadOnlyDictionary<int, CatalogSystemDefinition> SolarSystems => catalog.Systems;

    private async Task Authorize(ClaimsPrincipal user)
    {
        if (!(await authorization.AuthorizeAsync(user, null, AdministratorAccounts.Policy)).Succeeded || !administrators.IsCurrent(user))
        {
            throw new UnauthorizedAccessException("Sign in with the current local administrator account.");
        }
    }

    internal async Task<WorldWipePlan> WipeWorld(
        ClaimsPrincipal user,
        string confirmation,
        WorldRecreation recreation,
        long generation,
        WorldWipeRequest wipe
    )
    {
        await Authorize(user);
        var state = configuration.Read();
        if (state.Generation != generation)
        {
            throw new InvalidOperationException("The world settings changed. Reload the World page and review the latest revision.");
        }
        if (recreation == WorldRecreation.Reuse && state.Pending is { } pending)
        {
            configuration.Validate(pending.Draft);
        }
        return wipe.Prepare(confirmation, recreation, generation);
    }

    internal async Task<ConfigurationState> Read(ClaimsPrincipal user)
    {
        await Authorize(user);
        return configuration.Read();
    }

    internal async Task<ServerPerformanceSnapshot> Performance(ClaimsPrincipal user)
    {
        await Authorize(user);
        return performance.Read();
    }

    internal async Task<KillboardPage?> Killboard(ClaimsPrincipal user, KillboardFilter filter)
    {
        await Authorize(user);
        return killboard.Read(filter);
    }

    internal async Task<AdminPopulationSnapshot> Population(ClaimsPrincipal user)
    {
        await Authorize(user);
        return new(
            population.Summary(),
            capsuleers.Read(),
            scene.MaterializedPilots,
            population.Systems(),
            configuration.Read().Active.Draft.Settings.Capsuleers
        )
        {
            RegionalConflicts = regionalConflicts?.ReadConflicts() ?? [],
        };
    }

    internal async Task<CapsuleerCorporationNamePreview?> PreviewCorporationName(ClaimsPrincipal user, int id)
    {
        await Authorize(user);
        return corporationIdentities.Preview(id);
    }

    internal async Task<CapsuleerDirectoryPage> BrowsePopulation(
        ClaimsPrincipal user,
        CapsuleerDirectory directory,
        string? search,
        int page
    )
    {
        await Authorize(user);
        return inspection.Browse(directory, search, page);
    }

    internal async Task<CapsuleerPilotInspection?> InspectBot(ClaimsPrincipal user, long id)
    {
        await Authorize(user);
        return inspection.Pilot(id);
    }

    internal async Task<CapsuleerCorporationInspection?> InspectBotCorporation(ClaimsPrincipal user, int id)
    {
        await Authorize(user);
        return inspection.Corporation(id);
    }

    internal async Task<CapsuleerAllianceInspection?> InspectBotAlliance(ClaimsPrincipal user, int id)
    {
        await Authorize(user);
        return inspection.Alliance(id);
    }

    internal async Task<bool> ApplyCorporationName(ClaimsPrincipal user, CapsuleerCorporationNamePreview preview)
    {
        await Authorize(user);
        return corporationIdentities.Apply(preview);
    }

    internal async Task<CapsuleerRosterReport?> CorporationRoster(ClaimsPrincipal user, int id)
    {
        await Authorize(user);
        return CapsuleerCorporations.Roster(database, id);
    }

    internal async Task<ConfigurationRevision> Revision(ClaimsPrincipal user, long id)
    {
        await Authorize(user);
        return configuration.Get(id);
    }

    internal async Task<IReadOnlyList<ConfigurationRevisionInfo>> History(ClaimsPrincipal user, int skip = 0)
    {
        await Authorize(user);
        return configuration.History(skip);
    }

    internal async Task<AdminValidationResult> Validate(ClaimsPrincipal user, WorldConfigurationDraft draft)
    {
        await Authorize(user);
        try
        {
            configuration.Validate(draft);
            return new(true, []);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        {
            return new(false, [error.Message]);
        }
    }

    internal async Task<ConfigurationState> Save(ClaimsPrincipal user, AdminSaveRequest request)
    {
        await Authorize(user);
        return configuration.Save(request.Generation, request.Draft, user.Identity!.Name!);
    }

    internal async Task<ConfigurationState> Restore(ClaimsPrincipal user, AdminRestoreRequest request)
    {
        await Authorize(user);
        return configuration.Restore(request.Generation, request.RevisionId, user.Identity!.Name!);
    }

    internal async Task<ConfigurationState> Import(ClaimsPrincipal user, long generation, string document)
    {
        await Authorize(user);
        return configuration.Import(generation, document, user.Identity!.Name!);
    }

    internal async Task<IReadOnlyList<RevisionDifference>> Compare(ClaimsPrincipal user, long left, long right)
    {
        await Authorize(user);
        return ConfigurationDisplay.Compare(configuration.Get(left).Draft, configuration.Get(right).Draft);
    }

    internal async Task<IReadOnlyList<CatalogDatasetDescriptor>> Datasets(ClaimsPrincipal user)
    {
        await Authorize(user);
        return browser.Datasets;
    }

    internal async Task<CatalogDatasetDescriptor> Describe(ClaimsPrincipal user, string dataset)
    {
        await Authorize(user);
        return browser.Describe(dataset);
    }

    internal async Task<CatalogBrowsePage> Page(
        ClaimsPrincipal user,
        string dataset,
        string? after = null,
        int limit = 50,
        string? search = null,
        string? key = null
    )
    {
        await Authorize(user);
        return browser.Page(dataset, after, limit, search, key);
    }

    internal async Task<IReadOnlyList<CatalogRelatedRecord>> Relationships(ClaimsPrincipal user, string dataset, CatalogStoredRecord record)
    {
        await Authorize(user);
        return browser.Relationships(dataset, record);
    }

    internal async Task<decimal?> SourcePrice(ClaimsPrincipal user, int typeId)
    {
        await Authorize(user);
        double? price = catalog.Find(CatalogData.Client.Types, (long)typeId)?.BasePrice;
        return price is > 0 && double.IsFinite(price.Value) && price.Value <= (double)decimal.MaxValue ? (decimal)price.Value : null;
    }
}
