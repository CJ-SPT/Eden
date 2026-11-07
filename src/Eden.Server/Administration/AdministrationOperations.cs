using System.Security.Claims;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Gameplay.Configuration;
using Eden.World.Catalog;
using Microsoft.AspNetCore.Authorization;

namespace Eden.Server.Administration;

internal sealed record AdminSaveRequest(long Generation, WorldConfigurationDraft Draft);

internal sealed record AdminRestoreRequest(long Generation, long RevisionId);

internal sealed record AdminImportRequest(long Generation, string Document);

internal sealed record AdminValidationResult(bool Valid, IReadOnlyList<string> Messages);

internal sealed record AdminRecordView(CatalogStoredRecord Record, IReadOnlyList<CatalogRelatedRecord> Relationships);

internal sealed class AdministrationOperations(
    AdministratorAccounts administrators,
    IAuthorizationService authorization,
    IWorldConfiguration configuration,
    CatalogBrowser browser,
    CatalogProvider catalog,
    ServerPerformanceMonitor performance
)
{
    private async Task Authorize(ClaimsPrincipal user)
    {
        if (!(await authorization.AuthorizeAsync(user, null, AdministratorAccounts.Policy)).Succeeded || !administrators.IsCurrent(user))
        {
            throw new UnauthorizedAccessException("Sign in with the current local administrator account.");
        }
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
