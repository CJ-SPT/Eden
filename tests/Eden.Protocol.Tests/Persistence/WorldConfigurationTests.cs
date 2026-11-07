using System.Text.Json;
using Eden.Data.World.Configuration;
using Eden.Data.World.Economy;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Configuration;
using Eden.Server.Configuration;
using Eden.World.Configuration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class WorldConfigurationTests
{
    [Fact]
    public void RestartPoliciesAffectNewPilotGrantsAndSkillsWithoutChangingExistingPilots()
    {
        using TestWorld fixture = new();
        WorldDatabase database = new(fixture.Path);
        TestCatalog.AddSchool(database.CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        WorldConfigurationStore store = new(database, TimeProvider.System);
        var state = store.Read();
        store.Save(
            state.Generation,
            state.Active.Draft with
            {
                Settings = state.Active.Draft.Settings with
                {
                    Progression = new(false),
                    DevelopmentSupply = state.Active.Draft.Settings.DevelopmentSupply with { GrantCents = 12345 },
                },
            },
            "admin"
        );
        WorldInitialization.Initialize(database);
        var creation = fixture.Services.GetRequiredService<ICharacterCreation>();
        long pilot = creation.Create(1000001, new("Configured Pilot", 1, 1, 1, 64, [1], [2], 18));
        using WorldDbContext world = new(database.Path);
        Assert.Empty(world.Set<PilotSkill>().Where(skill => skill.PilotId == pilot));
        Assert.Equal(12345, world.Set<PilotWallet>().Find(pilot)!.Cents);
        Assert.Equal(12345, world.Set<TestingWalletGrant>().Find(pilot)!.AmountCents);
        Assert.NotEmpty(world.Set<PilotSkill>().Where(skill => skill.PilotId != pilot));
    }

    [Fact]
    public void LocalCommandsExportImportAndRestoreWithoutActivatingPendingEdits()
    {
        using TestWorld fixture = new();
        WorldConfigurationStore store = new(new(fixture.Path), TimeProvider.System);
        string output = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "configuration.json");
        Assert.True(ConfigurationCommands.TryRun(["--configuration-export", "1", "--output", output], fixture.Path));
        Assert.True(File.Exists(output));
        Assert.True(ConfigurationCommands.TryRun(["--configuration-import", output], fixture.Path));
        Assert.Equal(2, store.Read().Pending!.Id);
        Assert.Equal(1, store.Read().Active.Id);
        Assert.True(ConfigurationCommands.TryRun(["--configuration-restore", "1"], fixture.Path));
        Assert.Equal(3, store.Read().Pending!.Id);
        Assert.Equal(1, store.Read().Active.Id);
        Assert.Throws<InvalidDataException>(() => ConfigurationCommands.TryRun(["--configuration-import"], fixture.Path));
    }

    [Fact]
    public void StagingPreservesActiveValuesAndRestartIsolatesWorldsSharingOneCatalog()
    {
        using TestWorld fixture = new();
        WorldDatabase database = new(fixture.Path);
        WorldConfigurationStore store = new(database, TimeProvider.System);
        var original = store.Read();
        long balance;
        long deadline = DateTime.UtcNow.AddDays(7).ToFileTimeUtc();
        using (WorldDbContext world = new(database.Path))
        {
            balance = world.Set<PilotWallet>().Select(wallet => wallet.Cents).FirstOrDefault();
            world.Add(
                new SavedBeltRenewal
                {
                    BeltId = 123,
                    SystemId = 30000142,
                    NextRenewal = deadline,
                }
            );
            world.SaveChanges();
        }
        WorldDatabase other = new(Path.Combine(Path.GetDirectoryName(database.Path)!, "other.sqlite"));
        WorldInitialization.Initialize(other);
        Assert.Same(database.Catalog, other.Catalog);
        var draft = original.Active.Draft with
        {
            Settings = original.Active.Draft.Settings with { Resources = new(false, 3) },
            AdjustedPrices = [new(601, 123.456789m)],
            ManufacturingIndices = [new(30000142, 0.123456m)],
        };
        var staged = store.Save(original.Generation, draft, "Local administrator");
        Assert.Equal(original.Active, staged.Active);
        Assert.Equal(draft.Settings, staged.Pending!.Draft.Settings);
        Assert.Equal(draft.AdjustedPrices.ToArray(), staged.Pending.Draft.AdjustedPrices.ToArray());
        Assert.True(database.Settings.Resources.RenewBelts);
        WorldInitialization.Initialize(database);
        var active = store.Read();
        Assert.Null(active.Pending);
        Assert.Equal(staged.Pending.Id, active.Active.Id);
        Assert.False(database.Settings.Resources.RenewBelts);
        Assert.True(other.Settings.Resources.RenewBelts);
        Assert.Same(database.Catalog, other.Catalog);
        using WorldDbContext saved = new(database.Path);
        Assert.Equal(123.456789m, saved.Set<SavedAdjustedPrice>().Find(601)!.PriceIsk);
        Assert.Equal(0.123456m, saved.Set<SavedManufacturingIndex>().Find(30000142)!.Index);
        Assert.Equal(deadline, saved.Set<SavedBeltRenewal>().Find(123)!.NextRenewal);
        Assert.Equal(balance, saved.Set<PilotWallet>().Select(wallet => wallet.Cents).FirstOrDefault());
        using WorldDbContext untouched = new(other.Path);
        Assert.Empty(untouched.Set<SavedAdjustedPrice>());
    }

    [Fact]
    public void ValidationStaleWritesAndImmutableHistoryProtectTheSavedWorld()
    {
        using TestWorld fixture = new();
        WorldConfigurationStore store = new(new(fixture.Path), TimeProvider.System);
        var original = store.Read();
        var draft = original.Active.Draft;
        Assert.Throws<InvalidDataException>(() =>
            store.Save(original.Generation, draft with { AdjustedPrices = [new(int.MaxValue, 1)] }, "admin")
        );
        Assert.Throws<InvalidDataException>(() =>
            store.Validate(draft with { Settings = draft.Settings with { Resources = new(true, 24) } })
        );
        Assert.Throws<InvalidDataException>(() => store.Validate(draft with { CatalogFingerprint = "wrong" }));
        Assert.Throws<InvalidDataException>(() => store.Validate(draft with { ManufacturingIndices = [new(30000142, 2)] }));
        Assert.Throws<InvalidDataException>(() => store.Validate(draft with { AdjustedPrices = [new(601, 1), new(601, 2)] }));
        Assert.Throws<JsonException>(() =>
            store.Import(
                original.Generation,
                store.Export(original.Active.Id).Replace("\"formatVersion\": 1", "\"formatVersion\": 1, \"volume\": 7"),
                "admin"
            )
        );
        Assert.Single(store.History());
        var staged = store.Save(original.Generation, draft, "admin");
        Assert.Throws<InvalidOperationException>(() => store.Save(original.Generation, draft, "stale"));
        Assert.Equal(staged.Generation, store.Read().Generation);
        using WorldDbContext world = new(fixture.Path);
        Assert.Throws<SqliteException>(() =>
            world.Database.ExecuteSqlRaw("UPDATE WorldConfigurationRevision SET Author='changed' WHERE Id=1")
        );
        Assert.Equal("Saved-world migration", store.Get(1).Author);
    }

    [Fact]
    public void RestoreAndImportCreateNewPendingRevisionsWithoutRewritingHistory()
    {
        using TestWorld fixture = new();
        WorldConfigurationStore store = new(new(fixture.Path), TimeProvider.System);
        var initial = store.Read();
        string exported = store.Export(initial.Active.Id);
        var staged = store.Save(
            initial.Generation,
            initial.Active.Draft with
            {
                Settings = initial.Active.Draft.Settings with { Progression = new(false) },
            },
            "first"
        );
        var restored = store.Restore(staged.Generation, initial.Active.Id, "restore");
        Assert.Equal(staged.Pending!.Id, restored.Pending!.ParentId);
        Assert.Equal(initial.Active.Id, restored.Active.Id);
        Assert.True(restored.Pending.Draft.Settings.Progression.GrantStartingSkills);
        var imported = store.Import(restored.Generation, exported, "import");
        Assert.Equal(4, imported.Pending!.Id);
        Assert.Equal(exported, store.Export(initial.Active.Id));
        Assert.Equal(4, store.History().Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidPendingRevisionRetainsThePreviousActiveConfigurationAndReportsFailure(bool malformed)
    {
        using TestWorld fixture = new();
        WorldDatabase database = new(fixture.Path);
        WorldConfigurationStore store = new(database, TimeProvider.System);
        var initial = store.Read();
        string invalid = malformed ? "{" : store.Export(1).Replace("\"renewalHourUtc\": 11", "\"renewalHourUtc\": 99");
        using (WorldDbContext world = new(fixture.Path))
        {
            world.Add(
                new SavedConfigurationRevision
                {
                    Id = 2,
                    Author = "invalid fixture",
                    Document = invalid,
                }
            );
            world.SaveChanges();
            world.Database.ExecuteSqlRaw("UPDATE WorldConfigurationState SET PendingRevisionId=2");
        }
        WorldInitialization.Initialize(database);
        var rejected = store.Read();
        Assert.Equal(initial.Active.Id, rejected.Active.Id);
        Assert.Null(rejected.Pending);
        Assert.Contains("Revision 2 rejected", rejected.ActivationFailure);
        Assert.Equal(initial.Active.Draft.Settings, database.Settings);
        Assert.Equal(2, store.History().Count);
        Assert.Equal(3, store.Restore(rejected.Generation, 1, "restore").Pending!.Id);
    }

    [Fact]
    public void Version98UpgradeCapturesExistingPoliciesAndRepeatedStartupNeverResetsThem()
    {
        using TestWorld fixture = new();
        using (WorldDbContext world = new(fixture.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldConfigurationState;
                DROP TABLE WorldConfigurationRevision;
                UPDATE WorldEconomicPolicy SET RenewalHourUtc=3, RenewBelts=0, DefaultManufacturingIndex='0.17';
                UPDATE WorldEconomy SET InstitutionalStock=77, TestingGrants=0;
                PRAGMA user_version=98;
                """
            );
        }
        WorldDatabase database = new(fixture.Path);
        WorldInitialization.Initialize(database);
        WorldInitialization.Initialize(database);
        WorldConfigurationStore store = new(database, TimeProvider.System);
        var state = store.Read();
        Assert.Single(store.History());
        Assert.Equal(1, state.Generation);
        Assert.Equal(3, state.Active.Draft.Settings.Resources.RenewalHourUtc);
        Assert.False(state.Active.Draft.Settings.Resources.RenewBelts);
        Assert.Equal(0.17m, state.Active.Draft.Settings.Economy.DefaultManufacturingIndex);
        Assert.Equal(77, state.Active.Draft.Settings.DevelopmentSupply.InstitutionalStock);
        Assert.False(state.Active.Draft.Settings.DevelopmentSupply.TestingGrants);
    }

    [Fact]
    public void FailedStateWriteRollsBackTheNewRevisionAndAllowsAnUnchangedRetry()
    {
        using TestWorld fixture = new();
        WorldConfigurationStore store = new(new(fixture.Path), TimeProvider.System);
        var state = store.Read();
        using (WorldDbContext world = new(fixture.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER RejectConfiguration BEFORE UPDATE ON WorldConfigurationState
                BEGIN SELECT RAISE(ABORT,'interrupted configuration save'); END;
                """
            );
        }
        Assert.Throws<DbUpdateException>(() => store.Save(state.Generation, state.Active.Draft, "admin"));
        Assert.Single(store.History());
        Assert.Null(store.Read().Pending);
        using (WorldDbContext world = new(fixture.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectConfiguration");
        }
        Assert.Equal(2, store.Save(state.Generation, state.Active.Draft, "retry").Pending!.Id);
    }

    [Fact]
    public async Task DurablePendingSavesSurviveCheckpointFailureAndRetryWithoutEarlyActivation()
    {
        using TestWorld fixture = new();
        WorldDatabase database = new(fixture.Path);
        bool fail = true;
        using LiveWorld live = new(
            database,
            TimeProvider.System,
            persist: (snapshot, path) =>
            {
                if (fail)
                {
                    throw new IOException("interrupted checkpoint");
                }
                LiveWorld.PersistSnapshot(snapshot, path);
            }
        );
        WorldConfigurationStore store = new(database, TimeProvider.System);
        var state = store.Read();
        Assert.Throws<IOException>(() => store.Save(state.Generation, state.Active.Draft, "admin"));
        Assert.Equal(2, store.Read().Pending!.Id);
        Assert.Equal(1, store.Read().Active.Id);
        fail = false;
        await live.FlushAsync();
        using var disk = WorldConnections.FileConnection(database.Path);
        disk.Open();
        using var command = disk.CreateCommand();
        command.CommandText = "SELECT PendingRevisionId FROM WorldConfigurationState";
        Assert.Equal(2L, command.ExecuteScalar());
        Assert.Throws<InvalidOperationException>(() => WorldConfigurationStore.Activate(database));
    }
}
