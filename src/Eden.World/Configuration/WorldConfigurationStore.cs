using System.Collections.Immutable;
using Eden.Data.World.Configuration;
using Eden.Data.World.Economy;
using Eden.Data.World.Market;
using Eden.Gameplay.Configuration;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Configuration;

[Injectable]
public sealed class WorldConfigurationStore(WorldDatabase database, TimeProvider clock) : IWorldConfiguration
{
    private static ConfigurationRevision Revision(SavedConfigurationRevision row) =>
        new(row.Id, row.ParentId, row.Author, row.CreatedAt, ConfigurationValidation.Decode(row.Document));

    private static ConfigurationState Snapshot(WorldDbContext world, SavedConfigurationState state) =>
        new(
            state.Generation,
            Revision(world.Set<SavedConfigurationRevision>().Find(state.ActiveRevisionId)!),
            state.PendingRevisionId is long pending ? Revision(world.Set<SavedConfigurationRevision>().Find(pending)!) : null,
            state.ActivationFailure
        );

    public ConfigurationState Read()
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        return Snapshot(world, world.Set<SavedConfigurationState>().Single());
    }

    public IReadOnlyList<ConfigurationRevisionInfo> History(int skip = 0, int limit = 50)
    {
        if (skip < 0 || limit is < 1 or > 100)
        {
            throw new InvalidDataException("Invalid revision page.");
        }
        using WorldDbContext world = new(database.Path);
        return world
            .Set<SavedConfigurationRevision>()
            .OrderByDescending(revision => revision.Id)
            .Skip(skip)
            .Take(limit)
            .Select(revision => new ConfigurationRevisionInfo(revision.Id, revision.ParentId, revision.Author, revision.CreatedAt))
            .ToImmutableArray();
    }

    public void Validate(WorldConfigurationDraft draft) => ConfigurationValidation.Validate(draft, database.Catalog);

    public ConfigurationState Save(long expectedGeneration, WorldConfigurationDraft draft, string author)
    {
        Validate(draft);
        if (string.IsNullOrWhiteSpace(author) || author.Length > 128)
        {
            throw new InvalidDataException("A revision author of at most 128 characters is required.");
        }
        using WorldDbContext world = new(database.Path);
        world.Database.OpenConnection();
        using var transaction = world.Database.BeginTransaction();
        var state = world.Set<SavedConfigurationState>().Single();
        if (state.Generation != expectedGeneration)
        {
            throw new InvalidOperationException("Configuration changed. Reload the current revision before saving.");
        }
        long next = checked(world.Set<SavedConfigurationRevision>().Max(revision => revision.Id) + 1);
        world.Add(
            new SavedConfigurationRevision
            {
                Id = next,
                ParentId = state.PendingRevisionId ?? state.ActiveRevisionId,
                Author = author.Trim(),
                CreatedAt = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
                Document = ConfigurationValidation.Encode(draft),
            }
        );
        world.SaveChanges();
        state.PendingRevisionId = next;
        state.Generation = checked(state.Generation + 1);
        state.ActivationFailure = null;
        world.Update(state);
        world.SaveChanges();
        var result = Snapshot(world, state);
        transaction.Commit();
        WorldConnections.RequireDurableClose(world.Database.GetDbConnection());
        return result;
    }

    public ConfigurationState Restore(long expectedGeneration, long revisionId, string author) =>
        Save(expectedGeneration, Get(revisionId).Draft, author);

    public string Export(long revisionId) => ConfigurationValidation.Encode(Get(revisionId).Draft);

    public ConfigurationState Import(long expectedGeneration, string document, string author) =>
        Save(expectedGeneration, ConfigurationValidation.Decode(document), author);

    public ConfigurationRevision Get(long revisionId)
    {
        using WorldDbContext world = new(database.Path);
        return Revision(
            world.Set<SavedConfigurationRevision>().Find(revisionId) ?? throw new InvalidDataException("Unknown configuration revision.")
        );
    }

    internal static void Seed(WorldDbContext world, CatalogDbContext catalog, TimeProvider clock)
    {
        if (world.Set<SavedConfigurationState>().Any())
        {
            return;
        }
        var economy = world.Set<SavedEconomicPolicy>().Single();
        var supply = world.Set<SavedWorldEconomy>().Single();
        WorldConfigurationDraft draft = new()
        {
            CatalogFingerprint = catalog.Definitions.Identity.SourceFingerprint,
            Settings = new()
            {
                Economy = new(economy.ChargeServiceCosts, economy.DefaultManufacturingIndex),
                DevelopmentSupply = new(supply.TestingMarket, supply.TestingGrants, supply.InstitutionalStock),
                Resources = new(economy.RenewBelts, economy.RenewalHourUtc),
            },
            AdjustedPrices = world
                .Set<SavedAdjustedPrice>()
                .OrderBy(price => price.TypeId)
                .AsEnumerable()
                .Select(price => new AdjustedPriceOverride(price.TypeId, price.PriceIsk))
                .ToImmutableArray(),
            ManufacturingIndices = world
                .Set<SavedManufacturingIndex>()
                .OrderBy(index => index.SystemId)
                .AsEnumerable()
                .Select(index => new ManufacturingIndexOverride(index.SystemId, index.Index))
                .ToImmutableArray(),
        };
        ConfigurationValidation.Validate(draft, catalog.Definitions);
        world.Add(
            new SavedConfigurationRevision
            {
                Id = 1,
                Author = "Saved-world migration",
                CreatedAt = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
                Document = ConfigurationValidation.Encode(draft),
            }
        );
        world.SaveChanges();
        world.Add(new SavedConfigurationState { Generation = 1, ActiveRevisionId = 1 });
        world.SaveChanges();
    }

    internal static void LoadActive(WorldDatabase database)
    {
        if (!File.Exists(database.Path))
        {
            return;
        }
        using WorldDbContext world = new(database.Path);
        if (
            !world
                .Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE name='WorldConfigurationState'")
                .Single()
                .Equals(1)
        )
        {
            return;
        }
        var state = world.Set<SavedConfigurationState>().Single();
        database.Configuration.Install(Revision(world.Set<SavedConfigurationRevision>().Find(state.ActiveRevisionId)!));
    }

    internal static void Activate(WorldDatabase database)
    {
        if (Eden.World.Runtime.LiveWorld.Find(database.Path) is not null)
        {
            throw new InvalidOperationException("Configuration activation requires a stopped world.");
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var state = world.Set<SavedConfigurationState>().Single();
        var active = Revision(world.Set<SavedConfigurationRevision>().Find(state.ActiveRevisionId)!);
        ConfigurationValidation.Validate(active.Draft, database.Catalog);
        if (state.PendingRevisionId is long pendingId)
        {
            try
            {
                var pending = Revision(world.Set<SavedConfigurationRevision>().Find(pendingId)!);
                ConfigurationValidation.Validate(pending.Draft, database.Catalog);
                Apply(world, pending.Draft);
                state.ActiveRevisionId = pending.Id;
                state.PendingRevisionId = null;
                state.ActivationFailure = null;
                active = pending;
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or InvalidDataException or ArgumentException)
            {
                state.PendingRevisionId = null;
                state.ActivationFailure = $"Revision {pendingId} rejected: {error.Message}";
                ServerLog.Console.Warning($"Pending configuration {pendingId} rejected: {error.Message}");
            }
            state.Generation = checked(state.Generation + 1);
            world.Update(state);
            world.SaveChanges();
        }
        transaction.Commit();
        database.Configuration.Install(active);
    }

    private static void Apply(WorldDbContext world, WorldConfigurationDraft draft)
    {
        var economic = world.Set<SavedEconomicPolicy>().Single();
        economic.ChargeServiceCosts = draft.Settings.Economy.ChargeServiceCosts;
        economic.DefaultManufacturingIndex = draft.Settings.Economy.DefaultManufacturingIndex;
        economic.RenewBelts = draft.Settings.Resources.RenewBelts;
        economic.RenewalHourUtc = draft.Settings.Resources.RenewalHourUtc;
        world.Update(economic);
        var supply = world.Set<SavedWorldEconomy>().Single();
        supply.TestingMarket = draft.Settings.DevelopmentSupply.TestingMarket;
        supply.TestingGrants = draft.Settings.DevelopmentSupply.TestingGrants;
        supply.InstitutionalStock = draft.Settings.DevelopmentSupply.InstitutionalStock;
        world.Update(supply);
        world.Set<SavedAdjustedPrice>().ExecuteDelete();
        world.Set<SavedManufacturingIndex>().ExecuteDelete();
        world.AddRange(draft.AdjustedPrices.Select(price => new SavedAdjustedPrice { TypeId = price.TypeId, PriceIsk = price.PriceIsk }));
        world.AddRange(
            draft.ManufacturingIndices.Select(index => new SavedManufacturingIndex { SystemId = index.SystemId, Index = index.Index })
        );
    }
}
