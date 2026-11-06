using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Launcher;
using Eden.Server.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Launcher;

public sealed class PasswordConcurrencyTests
{
    private const string Original = "original password";
    private const string Replacement = "replacement password";

    private sealed class PausedPasswords : ILauncherPasswords, IDisposable
    {
        private readonly LauncherPasswords _inner = new();
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        internal bool PauseHash { get; set; }
        internal bool PauseVerify { get; set; }
        internal bool Rehash { get; set; }

        private void Pause()
        {
            Entered.Set();
            Assert.True(Release.Wait(TimeSpan.FromSeconds(10)));
        }

        public string Hash(string name, string password)
        {
            if (PauseHash)
            {
                Pause();
            }
            return _inner.Hash(name, password);
        }

        public PasswordVerificationResult Verify(string name, string? hash, string password)
        {
            if (PauseVerify)
            {
                Pause();
            }
            var result = _inner.Verify(name, hash, password);
            return Rehash && result != PasswordVerificationResult.Failed ? PasswordVerificationResult.SuccessRehashNeeded : result;
        }

        public void Dispose()
        {
            Release.Dispose();
            Entered.Dispose();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task VerificationPasswordChangesAndRehashingDoNotHoldTheWorld(int phase)
    {
        using TestWorld world = new(liveWorld: true);
        using PausedPasswords passwords = new();
        LauncherStore store = new(new(world.Path), TimeProvider.System, passwords);
        store.Initialize();
        store.Register(new("Concurrent pilot", Original));
        passwords.PauseVerify = phase == 0;
        passwords.PauseHash = phase != 0;
        passwords.Rehash = phase == 2;
        var operation = Task.Run(() =>
        {
            if (phase == 1)
            {
                store.SetPassword("Concurrent pilot", Replacement);
            }
            else
            {
                store.SignIn(new("Concurrent pilot", Original, false));
            }
        });
        try
        {
            Assert.True(passwords.Entered.Wait(TimeSpan.FromSeconds(10)));
            var read = Task.Run(() => world.Store.ReadAccount(1000001));
            Assert.Equal("Eden", (await read.WaitAsync(TimeSpan.FromSeconds(2))).UserName);
        }
        finally
        {
            passwords.Release.Set();
            await operation;
        }
    }

    [Fact]
    public async Task PasswordChangedDuringVerificationCannotCreateAStaleSession()
    {
        using TestWorld world = new(liveWorld: true);
        using PausedPasswords passwords = new();
        LauncherStore store = new(new(world.Path), TimeProvider.System, passwords);
        store.Initialize();
        store.Register(new("Changed pilot", Original));
        passwords.PauseVerify = true;
        var login = Task.Run(() => Assert.Throws<LauncherException>(() => store.SignIn(new("Changed pilot", Original, true))));
        try
        {
            Assert.True(passwords.Entered.Wait(TimeSpan.FromSeconds(10)));
            await Task.Run(() => store.SetPassword("Changed pilot", Replacement)).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            passwords.Release.Set();
            await login;
        }
        using WorldDbContext saved = new(world.Path);
        Assert.Equal(0, saved.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM LauncherSession").AsEnumerable().Single());
        Assert.NotNull(store.SignIn(new("Changed pilot", Replacement, false)).AccessToken);
    }
}
