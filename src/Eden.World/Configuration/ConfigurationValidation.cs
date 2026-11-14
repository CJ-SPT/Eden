using System.Text.Json;
using System.Text.Json.Serialization;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;

namespace Eden.World.Configuration;

internal static class ConfigurationValidation
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    internal static string Encode(WorldConfigurationDraft draft) => JsonSerializer.Serialize(draft, Json);

    internal static WorldConfigurationDraft Decode(string document) =>
        JsonSerializer.Deserialize<WorldConfigurationDraft>(document, Json)
        ?? throw new InvalidDataException("A configuration document is required.");

    internal static void Validate(WorldConfigurationDraft draft, CatalogProvider catalog)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.FormatVersion != 1 || draft.CatalogFingerprint != catalog.Identity.SourceFingerprint)
        {
            throw new InvalidDataException("Configuration format or installed catalog fingerprint does not match.");
        }
        var settings = draft.Settings;
        if (
            settings?.Economy is null
            || settings.DevelopmentSupply is null
            || settings.Resources is null
            || settings.MiningHolds is null
            || settings.NpcPopulation is null
            || settings.Progression is null
            || settings.Simulation is null
            || settings.Capsuleers is null
            || settings.Rewards is null
            || settings.PlanetaryIndustry is null
            || settings.Industry is null
            || settings.Navigation is null
        )
        {
            throw new InvalidDataException("Every typed settings group is required.");
        }
        WorldTuningRules.Validate(settings);
        Eden.Gameplay.Capsuleers.CapsuleerPopulationRules.Validate(settings.Capsuleers);
        var supply = settings.DevelopmentSupply;
        NpcSupplyRules.ValidateStock(supply.InstitutionalStock);
        if (
            settings.Economy.DefaultManufacturingIndex is < 0 or > 1
            || settings.Resources.RenewalHourUtc is < 0 or > 23
            || supply.TestingStock is < 1 or > 1_000_000_000
            || supply.TestingPriceCents is < 1
            || supply.TestingPriceCents > MarketDefinitions.MaximumOrderPrice * 100
            || supply.GrantCents is < 0 or > 100_000_000_000_000
            || settings.Simulation.CheckpointSeconds is < 1 or > 300
            || settings.Simulation.MarketPollMilliseconds is < 100 or > 60_000
            || settings.Simulation.ResourceCheckSeconds is < 1 or > 3600
        )
        {
            throw new InvalidDataException("A settings value is outside its supported range.");
        }
        if (
            draft.AdjustedPrices.IsDefault
            || draft.ManufacturingIndices.IsDefault
            || draft.AdjustedPrices.Length > 100_000
            || draft.ManufacturingIndices.Length > 20_000
            || draft.AdjustedPrices.Any(price =>
                price is null
                || !catalog.Items.ContainsKey(price.TypeId)
                || price.PriceIsk <= 0
                || price.PriceIsk > MarketDefinitions.MaximumOrderPrice
            )
            || draft.ManufacturingIndices.Any(index =>
                index is null || !catalog.Systems.ContainsKey(index.SystemId) || index.Index is < 0 or > 1
            )
            || draft.AdjustedPrices.Select(price => price.TypeId).Distinct().Count() != draft.AdjustedPrices.Length
            || draft.ManufacturingIndices.Select(index => index.SystemId).Distinct().Count() != draft.ManufacturingIndices.Length
        )
        {
            throw new InvalidDataException("Overrides require unique existing item/system IDs and valid prices or indices.");
        }
        if (settings.Economy.ChargeServiceCosts)
        {
            var prices = draft.AdjustedPrices.Select(price => price.TypeId).ToHashSet();
            if (
                catalog.Items.Values.Any(item =>
                    item.CategoryId == (int)InventoryCategory.Material
                    && catalog.Find(CatalogData.Client.Types, (long)item.Id)?.BasePrice is > 0
                    && !prices.Contains(item.Id)
                )
            )
            {
                throw new InvalidDataException("Service costs require adjusted prices for all priced catalog materials.");
            }
        }
    }
}
