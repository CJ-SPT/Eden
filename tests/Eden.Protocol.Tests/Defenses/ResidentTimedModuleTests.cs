using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Gameplay.Dogma;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Defenses;

public sealed partial class SignatureSuppressionOperationTests
{
    [Fact]
    public async Task ResidentMonitorRestoredDeadlinesResolveMembershipAfterTheCompleteImageLoads()
    {
        using var fixture = Prepare();
        long module = fixture.Fit(Basic);
        Space(fixture);
        long pilot = fixture.PilotId;
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        int system = fixture.Db.Store.FindPilot(1000001)!.SystemId;
        var paid = Store(fixture).Activate(pilot, module, fixture.Clock.Now);
        await live.FlushAsync();
        string copy = Path.Combine(Path.GetDirectoryName(fixture.Db.Path)!, "restored-module-deadlines.sqlite");
        File.Copy(fixture.Db.Path, copy);
        // Rehydrate the initialized checkpoint image, without the host's separate disconnect cleanup.
        using LiveWorld reopened = new(new(copy), fixture.Clock, _ => { });
        reopened.Economy.Warm();
        Assert.Equal(paid.Schedule.ExpiresAt, reopened.Economy.NextModuleExpiry(system));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(paid.Schedule.ExpiresAt - fixture.Clock.Now));
        using (var context = reopened.UseSystem(system))
        {
            reopened.Run(() =>
            {
                using var ownership = reopened.Enter();
            });
        }
        Assert.Equal(long.MaxValue, reopened.Economy.NextModuleExpiry(system));
        using var checkpoint = Assert.IsType<WorldSnapshot>(reopened.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext saved = new(checkpoint.Database);
        Assert.Null(saved.Set<ActiveFittedModule>().Find(module));
        Assert.Equal(paid.Schedule.ReadyAt, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }

    [Fact]
    public async Task AnUnrelatedResidentTransactionCannotPublishAnExpiredDefenseToAnotherSystem()
    {
        using var fixture = Prepare();
        long module = fixture.Fit(Basic);
        Space(fixture);
        long pilot = fixture.PilotId;
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        int system = fixture.Db.Store.FindPilot(1000001)!.SystemId;
        using CatalogDbContext catalog = new(fixture.CatalogPath);
        int unrelated = catalog.Definitions.Systems.Keys.First(value => value != system);
        double original = fixture.Fitting.ShipAttributes(pilot)[DogmaAttributeId.SignatureRadius];
        var paid = Store(fixture).Activate(pilot, module, fixture.Clock.Now);
        using (var warming = live.UseSystem(system))
        {
            Assert.True(fixture.Fitting.ShipAttributes(pilot)[DogmaAttributeId.SignatureRadius] < original);
        }
        using ManualResetEventSlim attempted = new();
        Task<double> reading;
        bool reachedBoundary;
        bool waited;
        using (var context = live.UseSystem(unrelated))
        using (var action = live.Economy.Begin(new(systems: [unrelated])))
        {
            fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(paid.Schedule.ExpiresAt - fixture.Clock.Now));
            reading = Task.Run(() =>
            {
                using var destination = live.UseSystem(system);
                attempted.Set();
                return fixture.Fitting.ShipAttributes(pilot)[DogmaAttributeId.SignatureRadius];
            });
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5)));
            reachedBoundary = SpinWait.SpinUntil(() => reading.IsCompleted || live.WaitingOwners > 0, TimeSpan.FromSeconds(5));
            waited = live.WaitingOwners > 0;
        }
        double observed = await reading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(reachedBoundary);
        Assert.True(waited);
        Assert.Equal(original, observed, 8);
        Assert.Equal(long.MaxValue, live.Economy.NextModuleExpiry(system));
    }

    [Fact]
    public void OfflineStopReturnsTheRestoredSignatureAndRetainsThePaidCooldown()
    {
        using var fixture = Prepare();
        long module = fixture.Fit(Basic);
        Space(fixture);
        long pilot = fixture.PilotId;
        long ship = fixture.ShipId;
        double before = fixture.Fitting.ShipAttributes(pilot)[DogmaAttributeId.SignatureRadius];
        var paid = Store(fixture).Activate(pilot, module, fixture.Clock.Now);
        Assert.True(paid.ShipAttributes[DogmaAttributeId.SignatureRadius] < before);
        var stopped = Store(fixture).Stop(pilot, ship, module);
        Assert.Equal(before, stopped[DogmaAttributeId.SignatureRadius], 8);
        using WorldDbContext saved = new(fixture.Db.Path);
        Assert.Null(saved.Set<ActiveFittedModule>().Find(module));
        Assert.Equal(paid.Schedule.ReadyAt, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }

    [Fact]
    public void ResidentDeadlineFollowsCommittedMembershipAndExpiresWithoutSqlOrCancellingTheCooldown()
    {
        using var fixture = Prepare();
        long module = fixture.Fit(Basic);
        Space(fixture);
        long pilot = fixture.PilotId;
        long ship = fixture.ShipId;
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var location = fixture.Db.Store.FindPilot(1000001)!;
        int source = location.SystemId;
        using CatalogDbContext catalog = new(fixture.CatalogPath);
        int destination = catalog.Definitions.Systems.Keys.First(system => system != source);
        var paid = Store(fixture).Activate(pilot, module, fixture.Clock.Now);
        Assert.Equal(paid.Schedule.ExpiresAt, live.Economy.NextModuleExpiry(source));
        using (var rejected = live.Economy.Begin(new(pilots: [pilot], systems: [source, destination])))
        {
            rejected.Positions.Find(ship)!.SystemId = destination;
        }
        Assert.Equal(paid.Schedule.ExpiresAt, live.Economy.NextModuleExpiry(source));
        Assert.Equal(long.MaxValue, live.Economy.NextModuleExpiry(destination));
        using (var transfer = live.Economy.Begin(new(pilots: [pilot], systems: [source, destination])))
        {
            transfer.Positions.Find(ship)!.SystemId = destination;
            transfer.Commit();
        }
        Assert.Equal(long.MaxValue, live.Economy.NextModuleExpiry(source));
        Assert.Equal(paid.Schedule.ExpiresAt, live.Economy.NextModuleExpiry(destination));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(paid.Schedule.ExpiresAt - fixture.Clock.Now));
        LiveTimedModules empty = new(live, source);
        LiveTimedModules target = new(live, destination);
        List<string> commands = [];
        using (var metrics = SessionPerformance.Begin("resident module deadline", _ => { }, force: true, command: commands.Add)!)
        {
            for (int frame = 0; frame < 256; frame++)
            {
                empty.Expire(fixture.Clock.Now);
            }
            Assert.Equal(paid.Schedule.ExpiresAt, live.Economy.NextModuleExpiry(destination));
            target.Expire(fixture.Clock.Now);
            Assert.Equal(long.MaxValue, live.Economy.NextModuleExpiry(destination));
            Assert.True(metrics.Commands == 0, string.Join(Environment.NewLine, commands));
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext saved = new(checkpoint.Database);
        Assert.Null(saved.Set<ActiveFittedModule>().Find(module));
        Assert.Equal(paid.Schedule.ReadyAt, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }
}
