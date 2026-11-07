using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Gameplay.Configuration;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Eden.Server.Administration.Components;

public partial class CatalogPage
{
    [SupplyParameterFromQuery(Name = "dataset")]
    public string? SelectedDataset { get; set; }

    [SupplyParameterFromQuery(Name = "key")]
    public string? SelectedKey { get; set; }
    private string Dataset = "client/types";
    private string DatasetSearch = "";
    private string Search = "";
    private string? Error;
    private IReadOnlyList<CatalogDatasetDescriptor> Datasets = [];
    private CatalogDatasetDescriptor? Description;
    private CatalogBrowsePage? Page;
    private readonly Dictionary<string, IReadOnlyList<CatalogRelatedRecord>> Links = [];

    protected override async Task OnParametersSetAsync()
    {
        Dataset = SelectedDataset ?? "client/types";
        await Load(null, SelectedKey);
    }

    private async Task Load(string? after, string? key = null)
    {
        try
        {
            var user = (await Authentication.GetAuthenticationStateAsync()).User;
            Datasets = await Operations.Datasets(user);
            Description = await Operations.Describe(user, Dataset);
            Page = await Operations.Page(user, Dataset, after, 25, Search, key);
            Links.Clear();
            foreach (var record in Page.Rows)
            {
                Links[record.Key] = await Operations.Relationships(user, Dataset, record);
            }
            Error = null;
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            Error = error.Message;
            Page = null;
            Description = null;
            Links.Clear();
        }
    }

    private Task SearchRecords() => Load(null);

    private Task NextPage() => Load(Page?.NextCursor);

    private void SelectDataset(ChangeEventArgs args) =>
        Navigation.NavigateTo("/catalog?dataset=" + Uri.EscapeDataString(args.Value?.ToString() ?? Dataset));

    private static string Reference(CatalogRelatedRecord link) =>
        "/catalog?dataset=" + Uri.EscapeDataString(link.Dataset) + "&key=" + Uri.EscapeDataString(link.Key);
}
