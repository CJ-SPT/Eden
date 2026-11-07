using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Gameplay.Configuration;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Eden.Server.Administration.Components;

public partial class OverrideEditor
{
    [Parameter, EditorRequired]
    public WorldConfigurationDraft Draft { get; set; } = new();

    [Parameter, EditorRequired]
    public WorldConfigurationDraft Active { get; set; } = new();

    [Parameter, EditorRequired]
    public WorldConfigurationDraft Original { get; set; } = new();

    [Parameter]
    public WorldConfigurationDraft? Pending { get; set; }

    [Parameter]
    public EventCallback<WorldConfigurationDraft> DraftChanged { get; set; }
    private const int PageSize = 20;
    private int TypeId;
    private decimal Price;
    private int SystemId;
    private decimal Index;
    private int PricePage;
    private int IndexPage;
    private string? Error;
    private readonly Dictionary<int, decimal?> SourcePrices = [];

    protected override async Task OnParametersSetAsync() => await LoadPrices();

    private async Task LoadPrices()
    {
        try
        {
            PricePage = Math.Min(PricePage, Math.Max(0, (Draft.AdjustedPrices.Length - 1) / PageSize));
            IndexPage = Math.Min(IndexPage, Math.Max(0, (Draft.ManufacturingIndices.Length - 1) / PageSize));
            SourcePrices.Clear();
            var user = (await Authentication.GetAuthenticationStateAsync()).User;
            foreach (var price in Draft.AdjustedPrices.OrderBy(value => value.TypeId).Skip(PricePage * PageSize).Take(PageSize))
            {
                SourcePrices[price.TypeId] = await Operations.SourcePrice(user, price.TypeId);
            }
        }
        catch (UnauthorizedAccessException error)
        {
            Error = error.Message;
        }
    }

    private async Task Change(WorldConfigurationDraft next)
    {
        try
        {
            var user = (await Authentication.GetAuthenticationStateAsync()).User;
            var validation = await Operations.Validate(user, next);
            if (!validation.Valid)
            {
                throw new InvalidDataException(string.Join(" ", validation.Messages));
            }
            await DraftChanged.InvokeAsync(next);
            Error = null;
        }
        catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException)
        {
            Error = error.Message;
        }
    }

    private Task SetPrice() =>
        Change(
            Draft with
            {
                AdjustedPrices = Draft
                    .AdjustedPrices.Where(value => value.TypeId != TypeId)
                    .Append(new(TypeId, Price))
                    .OrderBy(value => value.TypeId)
                    .ToImmutableArray(),
            }
        );

    private async Task ResetPrice()
    {
        try
        {
            var user = (await Authentication.GetAuthenticationStateAsync()).User;
            decimal? source = await Operations.SourcePrice(user, TypeId);
            await Change(
                Draft with
                {
                    AdjustedPrices = Draft
                        .AdjustedPrices.Where(value => value.TypeId != TypeId)
                        .Concat(source is decimal value ? [new AdjustedPriceOverride(TypeId, value)] : [])
                        .OrderBy(value => value.TypeId)
                        .ToImmutableArray(),
                }
            );
        }
        catch (UnauthorizedAccessException error)
        {
            Error = error.Message;
        }
    }

    private Task SetIndex() =>
        Change(
            Draft with
            {
                ManufacturingIndices = Draft
                    .ManufacturingIndices.Where(value => value.SystemId != SystemId)
                    .Append(new(SystemId, Index))
                    .OrderBy(value => value.SystemId)
                    .ToImmutableArray(),
            }
        );

    private Task ResetIndex() =>
        Change(
            Draft with
            {
                ManufacturingIndices = Draft
                    .ManufacturingIndices.Where(value => value.SystemId != SystemId)
                    .Concat(Original.ManufacturingIndices.Where(value => value.SystemId == SystemId))
                    .OrderBy(value => value.SystemId)
                    .ToImmutableArray(),
            }
        );

    private decimal OriginalIndex(int id) =>
        Original.ManufacturingIndices.FirstOrDefault(value => value.SystemId == id)?.Index
        ?? Original.Settings.Economy.DefaultManufacturingIndex;

    private void Edit(AdjustedPriceOverride price) => (TypeId, Price) = (price.TypeId, price.PriceIsk);

    private void Edit(ManufacturingIndexOverride index) => (SystemId, Index) = (index.SystemId, index.Index);

    private async Task NextPrices()
    {
        PricePage++;
        await LoadPrices();
    }

    private async Task PreviousPrices()
    {
        PricePage--;
        await LoadPrices();
    }

    private void NextIndices() => IndexPage++;

    private void PreviousIndices() => IndexPage--;

    private static string Amount(decimal? price) => price is decimal value ? value + " ISK" : "—";

    private static string ItemLink(int id) => "/catalog?dataset=client%2Ftypes&key=" + id;
}
