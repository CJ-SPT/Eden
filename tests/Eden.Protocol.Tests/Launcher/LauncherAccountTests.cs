using System.Security.Cryptography;
using Eden.Gameplay.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Authentication;
using Eden.Server.Launcher;
using Eden.Server.Persistence;
using Microsoft.Data.Sqlite;

namespace Eden.Protocol.Tests.Launcher;

public sealed class LauncherAccountTests
{
    private const string Password = "a test password with spaces";

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void RegistrationPersistsOmegaWithoutChangingExistingAccountGrades()
    {
        using TestWorld world = new();
        var database = new WorldDatabase(world.Path);
        var store = new LauncherStore(database, TimeProvider.System);
        store.Initialize();
        var account = store.Register(new("Omega Pilot", Password));

        var reopened = WorldInitialization.Open(database);
        var reopenedStore = new LauncherStore(database, TimeProvider.System);
        reopenedStore.Initialize();
        Assert.Equal(AccountCloneGrade.Omega, reopened.ReadAccount(account.AccountId).CloneGrade);
        Assert.Equal(AccountCloneGrade.Alpha, reopened.ReadAccount(1000001).CloneGrade);
        Assert.Equal(account.AccountId, reopenedStore.SignIn(new("Omega Pilot", Password, false)).AccountId);
        Assert.Equal((long)PilotRole.Player, reopenedStore.ReadRoles(account.AccountId));
        Assert.Empty(reopened.ListPilots(account.AccountId));
    }

    [Fact]
    public void RegistersEmptyAccountAndProtectsExistingIdentity()
    {
        using TestWorld world = new();
        var store = new LauncherStore(new WorldDatabase(world.Path), TimeProvider.System);
        store.Initialize();
        var account = store.Register(new("New Pilot", Password));
        Assert.True(account.AccountId > 1000002);
        Assert.Empty(world.Store.ListPilots(account.AccountId));
        Assert.Equal((long)PilotRole.Player, store.ReadRoles(account.AccountId));
        Assert.Throws<LauncherException>(() => store.Register(new(" new pilot ", Password)));
        Assert.Throws<LauncherException>(() => store.Register(new("Eden Wing", Password)));
        var session = store.SignIn(new("NEW PILOT", Password, false));
        Assert.Equal(account.AccountId, session.AccountId);
        Assert.Null(session.RefreshToken);
        Assert.False(session.Developer);
        Assert.Throws<LauncherException>(() => store.SignIn(new("New Pilot", "wrong password", false)));
        Assert.Throws<LauncherException>(() => store.SignIn(new("Missing", Password, false)));
        using var database = new SqliteConnection($"Data Source={world.Path};Pooling=False");
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "SELECT PasswordHash FROM LauncherCredential WHERE AccountId=" + account.AccountId;
        Assert.DoesNotContain(Password, (string)command.ExecuteScalar()!);
    }

    [Fact]
    public async Task ConcurrentRegistrationCreatesOnlyOneAccount()
    {
        using TestWorld world = new();
        var first = new LauncherStore(new WorldDatabase(world.Path), TimeProvider.System);
        var second = new LauncherStore(new WorldDatabase(world.Path), TimeProvider.System);
        first.Initialize();
        var results = await Task.WhenAll(
            new[] { first, second }.Select(store =>
                Task.Run(() =>
                {
                    try
                    {
                        store.Register(new("Concurrent", Password));
                        return true;
                    }
                    catch (LauncherException error) when (error.Code == "name_unavailable")
                    {
                        return false;
                    }
                })
            )
        );
        Assert.Single(results, result => result);
    }

    [Fact]
    public void RefreshRotatesAndReplayRevokesTheFamily()
    {
        using TestWorld world = new();
        var store = new LauncherStore(new WorldDatabase(world.Path), TimeProvider.System);
        store.Initialize();
        store.Register(new("Remembered", Password));
        var first = store.SignIn(new("Remembered", Password, true));
        var second = store.Refresh(first.RefreshToken!);
        Assert.NotEqual(first.AccessToken, second.AccessToken);
        Assert.Throws<LauncherException>(() => store.IssueTicket(first.AccessToken));
        Assert.NotNull(store.IssueTicket(second.AccessToken));
        Assert.Throws<LauncherException>(() => store.Refresh(first.RefreshToken!));
        Assert.Throws<LauncherException>(() => store.IssueTicket(second.AccessToken));
        Assert.Throws<LauncherException>(() => store.Refresh(second.RefreshToken!));
    }

    [Fact]
    public void TicketsAreSingleUseBoundToAccountAndRevokedOnSignOut()
    {
        using TestWorld world = new();
        var clock = new Clock();
        var store = new LauncherStore(new WorldDatabase(world.Path), clock);
        store.Initialize();
        var account = store.Register(new("Ticket User", Password));
        var session = store.SignIn(new("Ticket User", Password, true));
        string ticket = store.IssueTicket(session.AccessToken);
        Assert.Equal(account.AccountId, store.ConsumeTicket(ticket));
        Assert.Null(store.ConsumeTicket(ticket));
        string expired = store.IssueTicket(session.AccessToken);
        clock.Now += TimeSpan.FromSeconds(LauncherStore.TicketSeconds);
        Assert.Null(store.ConsumeTicket(expired));
        string revoked = store.IssueTicket(session.AccessToken);
        store.Revoke(session.AccessToken);
        Assert.Null(store.ConsumeTicket(revoked));
        Assert.Throws<LauncherException>(() => store.Refresh(session.RefreshToken!));
    }

    [Fact]
    public void ExpiredAccessAndRefreshAreRejected()
    {
        using TestWorld world = new();
        var clock = new Clock();
        var store = new LauncherStore(new WorldDatabase(world.Path), clock);
        store.Initialize();
        store.Register(new("Expiring", Password));
        var session = store.SignIn(new("Expiring", Password, true));
        clock.Now += TimeSpan.FromSeconds(LauncherStore.AccessSeconds);
        Assert.Throws<LauncherException>(() => store.IssueTicket(session.AccessToken));
        Assert.NotNull(store.Refresh(session.RefreshToken!));
        clock.Now += TimeSpan.FromDays(30);
        Assert.Throws<LauncherException>(() => store.Refresh(session.RefreshToken!));
    }

    [Fact]
    public void PasswordSetupPreservesPilotsAndRevokesOldSessions()
    {
        using TestWorld world = new();
        var store = new LauncherStore(new WorldDatabase(world.Path), TimeProvider.System);
        store.Initialize();
        var pilot = world.Store.ListPilots(1000001).Single();
        store.SetPassword("Eden", Password);
        var session = store.SignIn(new("Eden", Password, true));
        store.SetPassword("Eden", Password + " changed");
        Assert.Throws<LauncherException>(() => store.Refresh(session.RefreshToken!));
        Assert.Equal(pilot.PilotId, world.Store.ListPilots(1000001).Single().PilotId);
        store.Initialize();
        Assert.Equal(1000001, store.SignIn(new("Eden", Password + " changed", false)).AccountId);
    }

    [Fact]
    public void ArbitraryManifestSigningAndImplicitDevelopmentLoginAreRejected()
    {
        using TestWorld world = new();
        var store = new LauncherStore(new WorldDatabase(world.Path), TimeProvider.System);
        store.Initialize();
        using var identity = new DevelopmentIdentity(TimeProvider.System, store, new LauncherOptions());
        Assert.Throws<LauncherException>(() => identity.SignManifest(Convert.ToBase64String(RandomNumberGenerator.GetBytes(17769))));
        Assert.Throws<LauncherException>(() => identity.SignManifest("not base64"));
        Assert.Null(identity.ValidateTicket("eden2.1000001.1.BAD.BAD"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void PasswordLengthLimitsApplyToRegistrationAndPasswordChanges(int length, bool accepted)
    {
        using TestWorld world = new();
        var database = new WorldDatabase(world.Path);
        var store = new LauncherStore(database, TimeProvider.System);
        store.Initialize();
        store.SetPassword("Eden", Password);
        var previous = store.SignIn(new("Eden", Password, true));
        string password = new('p', length);
        if (!accepted)
        {
            var registration = Assert.Throws<LauncherException>(() => store.Register(new("Length User", password)));
            Assert.Equal("invalid_password", registration.Code);
            Assert.Contains("6–128", registration.Message);
            var reset = Assert.Throws<LauncherException>(() => store.SetPassword("Eden", password));
            Assert.Equal("invalid_password", reset.Code);
            using WorldDbContext saved = new(world.Path);
            Assert.False(saved.Set<Account>().Any(account => account.UserName == "Length User"));
            Assert.Equal(previous.AccountId, store.SignIn(new("Eden", Password, false)).AccountId);
            Assert.NotNull(store.IssueTicket(previous.AccessToken));
            return;
        }
        var account = store.Register(new("Length User", password));
        store.SetPassword("Eden", password);
        var reopened = new LauncherStore(database, TimeProvider.System);
        reopened.Initialize();
        Assert.Equal(account.AccountId, reopened.SignIn(new("Length User", password, false)).AccountId);
        Assert.Equal(previous.AccountId, reopened.SignIn(new("Eden", password, false)).AccountId);
        Assert.Throws<LauncherException>(() => reopened.IssueTicket(previous.AccessToken));
    }

    [Theory]
    [InlineData("ab", "long enough test password")]
    [InlineData("Valid", "short")]
    [InlineData("wrong/name", "long enough test password")]
    public void InvalidRegistrationDoesNotCreateAccounts(string name, string password)
    {
        using TestWorld world = new();
        var store = new LauncherStore(new WorldDatabase(world.Path), TimeProvider.System);
        store.Initialize();
        Assert.Throws<LauncherException>(() => store.Register(new(name, password)));
    }

    [Fact]
    public void RemoteHostingRequiresTlsAndRejectsDevelopmentLogin()
    {
        Assert.Throws<InvalidDataException>(() => new LauncherOptions { ListenAddress = "0.0.0.0" }.Validate());
        Assert.Throws<InvalidDataException>(() =>
            new LauncherOptions
            {
                ListenAddress = "0.0.0.0",
                CertificatePath = "server.pfx",
                GatewayAddress = "https://eden.example:26001",
                DevelopmentLogin = true,
            }.Validate()
        );
        new LauncherOptions
        {
            ListenAddress = "0.0.0.0",
            CertificatePath = "server.pfx",
            GatewayAddress = "https://eden.example:26001",
        }.Validate();
    }
}
