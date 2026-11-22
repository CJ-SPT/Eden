using System.Security.Cryptography;
using Eden.Data.World.Characters;
using Eden.Data.World.Docking;
using Eden.Data.World.Launcher;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Server;
using Eden.Server.Administration;
using Eden.Server.Launcher;
using Eden.World.Configuration;
using Eden.World.Modding;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Administration;

public sealed class WorldWipeTests
{
    private const string AccountPassword = "world wipe account password";

    [Fact]
    public async Task WipeBacksUpFinalResidentStateAndRetainsAccountsAcrossRestart()
    {
        using TestWorld fixture = new(liveWorld: true);
        var database = new WorldDatabase(fixture.Path);
        TestCatalog.AddSchool(database.CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var launcher = fixture.Services.GetRequiredService<LauncherStore>();
        var account = launcher.Register(new("Wipe Survivor", AccountPassword));
        var session = launcher.SignIn(new("Wipe Survivor", AccountPassword, true));
        string ticket = await launcher.IssueTicketAsync(session.AccessToken);
        var mods = fixture.Services.GetRequiredService<ModStore>();
        string identity = mods.WorldIdentity();
        mods.WriteState("test.world", "pilot", "90000001");
        var config = new WorldConfigurationStore(database, TimeProvider.System).Read();
        var pilot = fixture.Store.FindPilot(1000001)!;
        SpacePosition finalPosition = new(101, 202, 303);
        fixture.Store.SetPosition(pilot.ShipId, finalPosition, pilot.HomeSystemId);
        long accounts;
        string credential;
        using (WorldDbContext world = new(fixture.Path))
        {
            accounts = world.Set<Account>().LongCount();
            credential = world.Set<SavedLauncherCredential>().Single(row => row.AccountId == account.AccountId).PasswordHash;
        }

        live.Dispose();
        string backup = fixture.Path + ".backups/test";
        WorldWipe.Execute(fixture.Path, backup);

        Assert.Null(WorldSetupStore.Read(fixture.Path));
        string backupWorld = Path.Combine(backup, "world.sqlite");
        using (WorldDbContext original = new(backupWorld))
        {
            Assert.NotEmpty(original.Set<Pilot>());
            Assert.NotEmpty(original.Set<SavedLauncherSession>());
            var position = original.Set<FlightPosition>().Single(row => row.ShipId == pilot.ShipId);
            Assert.Equal(finalPosition, new SpacePosition(position.X, position.Y, position.Z));
        }
        Assert.Equal("90000001", new ModStore(new(backupWorld)).ReadState("test.world", "pilot"));
        Assert.Null(mods.ReadState("test.world", "pilot"));
        Assert.Equal(identity, mods.WorldIdentity());
        AssertOnlyAccountAndConfigurationRowsRemain(fixture.Path);
        using (WorldDbContext wiped = new(fixture.Path))
        {
            Assert.Equal(accounts, wiped.Set<Account>().LongCount());
            Assert.Equal(credential, wiped.Set<SavedLauncherCredential>().Single(row => row.AccountId == account.AccountId).PasswordHash);
            Assert.Empty(wiped.Set<Pilot>());
        }

        WorldInitialization.Initialize(database);
        using LiveWorld reopened = new(database, TimeProvider.System, _ => { });
        var reopenedLauncher = new LauncherStore(database, TimeProvider.System);
        Assert.Equal(account.AccountId, reopenedLauncher.SignIn(new("Wipe Survivor", AccountPassword, false)).AccountId);
        Assert.Throws<LauncherException>(() => reopenedLauncher.Refresh(session.RefreshToken!));
        Assert.Throws<LauncherException>(() => reopenedLauncher.AuthorizeAccess(session.AccessToken));
        Assert.Null(await reopenedLauncher.ConsumeTicketAsync(ticket));
        Assert.Empty(fixture.Store.ListPilots(account.AccountId));
        Assert.Empty(fixture.Store.ListPilots(1000001));
        Assert.Equal(config, new WorldConfigurationStore(database, TimeProvider.System).Read());
        Assert.Equal("Eden", fixture.Store.ReadAccount(1000001).UserName);
        Assert.Equal(AccountCloneGrade.Omega, fixture.Store.ReadAccount(account.AccountId).CloneGrade);
        long newPilot = fixture
            .Services.GetRequiredService<ICharacterCreation>()
            .Create(account.AccountId, new("Fresh Pilot", 1, 1, 1, 64, [1], [2], 18));
        Assert.Equal(newPilot, Assert.Single(fixture.Store.ListPilots(account.AccountId)).PilotId);
        reopened.Dispose();
        Assert.Equal(newPilot, Assert.Single(WorldInitialization.Open(database).ListPilots(account.AccountId)).PilotId);
    }

    [Fact]
    public void ReuseAppliesSavedPendingRevisionWithoutReturningToSetup()
    {
        using TestWorld fixture = new(liveWorld: true);
        WorldDatabase database = new(fixture.Path);
        WorldConfigurationStore configuration = new(database, TimeProvider.System);
        var active = configuration.Read();
        var draft = active.Active.Draft;
        var rates = draft.Settings.Progression with { TrainingSpeedMultiplier = 2 };
        var pending = configuration.Save(
            active.Generation,
            draft with
            {
                Settings = draft.Settings with { Progression = rates },
            },
            "Operator"
        );
        fixture.Services.GetRequiredService<LiveWorld>().Dispose();

        WorldWipe.Execute(fixture.Path, fixture.Path + ".backups/reuse", configurationGeneration: pending.Generation);
        Assert.Null(WorldSetupStore.Read(fixture.Path));
        WorldInitialization.Initialize(database);
        var replacement = configuration.Read();
        Assert.Equal(pending.Pending!.Id, replacement.Active.Id);
        Assert.Null(replacement.Pending);
        Assert.Equal(rates, database.Settings.Progression);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReconfigurationSurvivesWipeAndUsesEstablishedSettingsWithExistingAccounts(bool legacyFresh)
    {
        using TestWorld fixture = new();
        string path = legacyFresh ? Path.Combine(Path.GetDirectoryName(fixture.Path)!, "legacy-world.sqlite") : fixture.Path;
        WorldDatabase database = new(path);
        if (legacyFresh)
        {
            WorldInitialization.Initialize(database, economy: new(FreshUniverse: true));
            Assert.True(database.Settings.UsesFreshUniverseMarket());
            WorldInitialization.Initialize(database);
            Assert.True(database.Settings.UsesFreshUniverseMarket());
            Assert.Null(WorldSetupStore.Read(path));
        }
        using var host = ServerServices.Build(database, liveWorld: true);
        var launcher = host.GetRequiredService<LauncherStore>();
        long accountId = launcher.Register(new("Retained Pilot", AccountPassword)).AccountId;
        var oldSession = launcher.SignIn(new("Retained Pilot", AccountPassword, true));
        WorldConfigurationStore configuration = new(database, TimeProvider.System);
        var active = configuration.Read();
        var draft = active.Active.Draft;
        var pending = configuration.Save(
            active.Generation,
            draft with
            {
                Settings = draft.Settings with { Progression = draft.Settings.Progression with { TrainingSpeedMultiplier = 2 } },
            },
            "Operator"
        );
        host.GetRequiredService<LiveWorld>().Dispose();

        string backup = path + ".backups/reconfigure";
        Assert.Throws<InvalidOperationException>(() =>
            WorldWipe.Execute(path, backup, reconfigure: !legacyFresh, configurationGeneration: active.Generation)
        );
        Assert.Null(WorldSetupStore.Read(path));
        WorldWipe.Execute(path, backup, reconfigure: !legacyFresh, configurationGeneration: pending.Generation);
        Assert.Null(WorldSetupStore.Read(Path.Combine(backup, "world.sqlite")));
        var state = WorldSetupStore.Read(path)!;
        Assert.False(state.NewWorld);
        Assert.Null(state.Approval);

        WorldSetupRequest request = new(database, state);
        request.Prepare("", "Operator");
        var approval = WorldSetupStore.Read(path)!.Approval!;
        WorldInitialization.Initialize(database, setup: approval);
        Assert.Null(WorldSetupStore.Read(path));
        Assert.False(database.Settings.FreshUniverse);
        Assert.Equal(legacyFresh, database.Settings.Capsuleers.EarnedPvpShips);
        if (legacyFresh)
        {
            Assert.True(database.Settings.Economy is { BeginnerMarketSupply: true, ProgressiveMarkets: true });
            Assert.False(database.Settings.DevelopmentSupply.TestingGrants);
        }
        Assert.Equal(draft.Settings.Progression.TrainingSpeedMultiplier, database.Settings.Progression.TrainingSpeedMultiplier);
        Assert.Null(configuration.Read().Pending);
        Assert.Equal(pending.Pending!.Id, configuration.Get(pending.Pending.Id).Id);
        using LiveWorld reopened = new(database, TimeProvider.System, _ => { });
        var reopenedLauncher = new LauncherStore(database, TimeProvider.System);
        Assert.Equal(accountId, reopenedLauncher.SignIn(new("Retained Pilot", AccountPassword, false)).AccountId);
        Assert.Throws<LauncherException>(() => reopenedLauncher.Refresh(oldSession.RefreshToken!));
        using var world = reopened.Economy.Begin();
        Assert.False(world.Pilots.Any(pilot => pilot.AccountId == accountId));
    }

    [Fact]
    public void FailedBackupAndOwnedWorldNeverDeleteSavedState()
    {
        using TestWorld fixture = new(liveWorld: true);
        string backup = fixture.Path + ".backups/blocked";
        Assert.Throws<InvalidOperationException>(() => WorldWipe.Execute(fixture.Path, backup));
        Assert.False(Directory.Exists(backup));
        fixture.Services.GetRequiredService<LiveWorld>().Dispose();
        byte[] original = SHA256.HashData(File.ReadAllBytes(fixture.Path));
        // An existing file blocks creating the parent backup directory.
        File.WriteAllText(fixture.Path + ".backups", "block backup creation");
        Assert.Throws<IOException>(() => WorldWipe.Execute(fixture.Path, backup));
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(fixture.Path)));
        Assert.NotEmpty(fixture.Store.ListPilots(1000001));
    }

    [Fact]
    public async Task ConfirmationIsExactAndShutdownFailurePreventsWipe()
    {
        using TestWorld fixture = new(liveWorld: true);
        WorldWipeRequest request = new(fixture.Path);
        Assert.Throws<InvalidDataException>(() => request.Prepare("wipe world"));
        Assert.Null(request.Pending);
        Assert.False(request.ShutdownRequested.IsCompleted);
        var plan = request.Prepare(WorldWipeRequest.Confirmation);
        Assert.Throws<InvalidOperationException>(() => request.Prepare(WorldWipeRequest.Confirmation));
        Assert.False(request.ShutdownRequested.IsCompleted);
        await request.BeginShutdown();
        Assert.True(request.ShutdownRequested.IsCompletedSuccessfully);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await ServerLifetime.RunAsync(lifetime =>
            {
                lifetime.OnStop("world", fixture.Services.GetRequiredService<LiveWorld>().Dispose);
                lifetime.OnStop("producer", () => throw new IOException("Producer could not stop."));
                return Task.CompletedTask;
            });
            WorldWipe.Execute(fixture.Path, plan.BackupDirectory);
        });
        Assert.False(Directory.Exists(plan.BackupDirectory));
        Assert.NotEmpty(fixture.Store.ListPilots(1000001));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PartialBackupOrWorldReplacementFailurePreservesOriginalWorldAndModState(bool failBackup)
    {
        using TestWorld fixture = new(liveWorld: true);
        var mods = fixture.Services.GetRequiredService<ModStore>();
        mods.WriteState("test.world", "pilot", "90000001");
        fixture.Services.GetRequiredService<LiveWorld>().Dispose();
        byte[] original = SHA256.HashData(File.ReadAllBytes(fixture.Path));
        string backup = fixture.Path + ".backups/failure";
        string failurePath = Path.GetFullPath(failBackup ? Path.Combine(backup, "world.sqlite.mods.sqlite") : fixture.Path);
        Assert.Throws<IOException>(() =>
            WorldWipe.Execute(
                fixture.Path,
                backup,
                (image, path) =>
                {
                    if (path == failurePath)
                    {
                        throw new IOException("Injected disk write failure.");
                    }
                    LiveWorld.PersistSnapshot(image, path);
                },
                reconfigure: true
            )
        );
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(fixture.Path)));
        Assert.Null(WorldSetupStore.Read(fixture.Path));
        Assert.Equal("90000001", mods.ReadState("test.world", "pilot"));
        Assert.NotEmpty(fixture.Store.ListPilots(1000001));
        using WorldDbContext saved = new(Path.Combine(backup, "world.sqlite"));
        Assert.NotEmpty(saved.Set<Pilot>());
        Assert.Throws<IOException>(() => WorldWipe.Execute(fixture.Path, backup));
    }

    private static void AssertOnlyAccountAndConfigurationRowsRemain(string path)
    {
        string[] retained =
        [
            "WorldAccount",
            "LauncherCredential",
            "WorldConfigurationRevision",
            "WorldConfigurationState",
            "WorldEconomy",
            "WorldEconomicPolicy",
            "WorldAdjustedPrice",
            "WorldManufacturingIndex",
        ];
        using SqliteConnection connection = WorldConnections.FileConnection(path);
        connection.Open();
        List<string> tables = [];
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            using var rows = query.ExecuteReader();
            while (rows.Read())
            {
                tables.Add(rows.GetString(0));
            }
        }
        foreach (string table in tables.Except(retained))
        {
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            Assert.True(Convert.ToInt64(query.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0, table);
        }
    }
}
