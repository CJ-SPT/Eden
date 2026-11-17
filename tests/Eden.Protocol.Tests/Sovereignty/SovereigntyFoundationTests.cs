using Eden.Data.World.Alliances;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Sovereignty;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyFoundationTests
{
    private const int FirstSystem = 30003995;
    private const int SecondSystem = 30003996;
    private const int AllianceId = AllianceRules.FirstAllianceId;
    private const long FirstHub = 1400000000001;

    [Fact]
    public async Task ResidentClaimsRollbackCaptureCoherentlyAndSurviveReopen()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        int corporation = CorporationAdministrationTests.CreateAndJoin(fixture);
        var resident = fixture.Services.GetRequiredService<LiveWorld>();
        var claims = fixture.Services.GetRequiredService<ISovereignty>();
        Assert.Null(claims.Read(FirstSystem));
        Assert.Empty(claims.List(AllianceId));
        using (var action = resident.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(
                new SavedAlliance
                {
                    Id = AllianceId,
                    Name = "Sovereignty Foundation",
                    Ticker = "SOV",
                }
            );
            action.Add(new SavedAllianceMember { CorporationId = corporation, AllianceId = AllianceId });
            action.Add(Claim(FirstSystem, FirstHub));
            action.Add(Claim(SecondSystem, FirstHub + 1));
            action.Commit();
        }
        var original = claims.Read(FirstSystem)!;
        Assert.Equal(new(FirstSystem, FirstHub, corporation, AllianceId, clock.Now), original);
        Assert.Equal(new[] { FirstSystem, SecondSystem }, claims.List(AllianceId).Select(row => row.SystemId));
        Assert.Empty(claims.List(AllianceId + 1));
        using (var action = resident.Economy.Begin(TransactionParticipants.None))
        {
            action.Set<SavedSovereigntyClaim>().Find(FirstSystem)!.HubId = FirstHub + 2;
            action.Remove(action.Set<SavedSovereigntyClaim>().Find(SecondSystem)!);
        }
        Assert.Equal(original, claims.Read(FirstSystem));
        Assert.Equal(2, claims.List(AllianceId).Count);

        // A detached capture retains its generation while resident ownership changes.
        using (var captured = Assert.IsType<WorldSnapshot>(resident.CaptureSnapshot()))
        {
            using (var action = resident.Economy.Begin(TransactionParticipants.None))
            {
                action.Set<SavedSovereigntyClaim>().Find(FirstSystem)!.HubId = FirstHub + 2;
                action.Commit();
            }
            captured.Project();
            using WorldDbContext snapshot = new(captured.Database);
            Assert.Equal(FirstHub, snapshot.Set<SavedSovereigntyClaim>().Find(FirstSystem)!.HubId);
            Assert.Equal(FirstHub + 2, claims.Read(FirstSystem)!.HubId);
            Assert.Equal(FirstHub, original.HubId);
        }
        using (var metrics = SessionPerformance.Begin("resident sovereignty reads", _ => { }, force: true)!)
        {
            for (int cycle = 0; cycle < 10; cycle++)
            {
                Assert.Equal(FirstHub + 2, claims.Read(FirstSystem)!.HubId);
                Assert.Null(claims.Read(FirstSystem - 1));
                Assert.Equal(2, claims.List(AllianceId).Count);
                Assert.Empty(claims.List(AllianceId + 1));
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        await resident.FlushAsync();
        fixture.Services.Dispose();
        using (var reopened = ServerServices.Build(new(fixture.Path), clock, liveWorld: true))
        {
            var restored = reopened.GetRequiredService<ISovereignty>();
            Assert.Equal(original with { HubId = FirstHub + 2 }, restored.Read(FirstSystem));
            Assert.Equal(2, restored.List(AllianceId).Count);
            var live = reopened.GetRequiredService<LiveWorld>();
            using (var action = live.Economy.Begin(TransactionParticipants.None))
            {
                action.Remove(action.Set<SavedSovereigntyClaim>().Find(FirstSystem)!);
                action.Commit();
            }
            Assert.Null(restored.Read(FirstSystem));
            Assert.Equal(SecondSystem, Assert.Single(restored.List(AllianceId)).SystemId);
            await live.FlushAsync();
        }
        using var final = ServerServices.Build(new(fixture.Path), clock, liveWorld: true);
        Assert.Null(final.GetRequiredService<ISovereignty>().Read(FirstSystem));
        Assert.Equal(SecondSystem, Assert.Single(final.GetRequiredService<ISovereignty>().List(AllianceId)).SystemId);

        SavedSovereigntyClaim Claim(int system, long hub) =>
            new()
            {
                SystemId = system,
                HubId = hub,
                CorporationId = corporation,
                AllianceId = AllianceId,
                ClaimedAt = clock.Now,
            };
    }

    [Fact]
    public void Version178UpgradesWithoutInventingClaimsOrChangingPilots()
    {
        using TestWorld fixture = new();
        var pilot = fixture.Store.FindPilot(1000001)!;
        fixture.Services.Dispose();
        using (WorldDbContext world = new(fixture.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldSovereigntyClaim; PRAGMA user_version=178;");
        }
        WorldInitialization.Initialize(new(fixture.Path));
        using var reopened = ServerServices.Build(new(fixture.Path), liveWorld: true);
        var claims = reopened.GetRequiredService<ISovereignty>();
        Assert.Null(claims.Read(FirstSystem));
        Assert.Empty(claims.List(AllianceId));
        Assert.Equal(pilot, reopened.GetRequiredService<Eden.World.Docking.SqliteWorld>().FindPilot(1000001));
        using WorldDbContext schema = new(fixture.Path);
        Assert.Equal(WorldMigration.CurrentVersion, schema.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }
}
