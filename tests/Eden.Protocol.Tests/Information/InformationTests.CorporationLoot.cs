using Eden.Data.World.Combat;
using Eden.Data.World.Loot;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.PilotSessions;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Information;

public sealed partial class InformationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemporaryTractorRightsEndAtTheOwnersExactSuspectDeadline(bool resident)
    {
        using Fixture f = Prepare();
        long module = f.Fit(24348);
        Space(f);
        long wreck = Wreck(f, 10000);
        var owner = f.Db.Store.FindPilot(1000002)!;
        long expiry = f.Clock.Now + TimeSpan.TicksPerSecond * 3 / 2;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldWreck>().Find(wreck)!.OwnerId = owner.PilotId;
            world.Add(
                new SavedCrimewatch
                {
                    PilotId = owner.PilotId,
                    SystemId = owner.SystemId,
                    SuspectUntil = expiry,
                }
            );
            world.SaveChanges();
        }
        using LiveWorld? live = resident ? new(new(f.Db.Path), f.Clock, _ => { }) : null;
        var activation = Store(f).Begin(f.PilotId, module, DogmaEffect.TractorBeam, wreck, new HashSet<long> { wreck }, f.Clock.Now);
        Store(f).AdvanceTractor(activation.Cycle, f.Clock.Now + TimeSpan.TicksPerSecond);
        double x = f.Db.Store.FindPilot(1000001)!.Position!.X;
        using (var metrics = SessionPerformance.Begin("expired tractor permission", _ => { }, force: true)!)
        {
            live?.Tractors.Advance(expiry);
            Assert.Throws<InvalidDataException>(() => Store(f).AdvanceTractor(activation.Cycle, expiry));
            if (resident)
            {
                Assert.Equal(x + 9500, live!.Tractors.Position(wreck)!.X, 5);
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
        }
        using WorldDbContext final = new(f.Db.Path);
        Assert.False(final.Set<WorldWreck>().Find(wreck)!.Abandoned);
        Assert.Equal(owner.PilotId, final.Set<WorldWreck>().Find(wreck)!.OwnerId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorporationTractorAccessUsesCurrentMembershipAndStopsOnDeparture(bool resident)
    {
        using Fixture f = Prepare();
        var owner = f.Db.Store.FindPilot(1000002)!;
        PlayerCorporationTests.Prepare(f.Db, owner.PilotId);
        int corporation = f
            .Db.Services.GetRequiredService<IPlayerCorporations>()
            .Create(owner.PilotId, PlayerCorporationTests.Details())
            .Corporation.Id;
        long module = f.Fit(24348);
        Space(f);
        long wreck = Wreck(f, 10000);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var row = world.Set<WorldWreck>().Find(wreck)!;
            row.OwnerId = owner.PilotId;
            row.CorporationId = corporation;
            world.SaveChanges();
        }
        using LiveWorld? live = resident ? new(new(f.Db.Path), f.Clock, _ => { }) : null;
        Assert.Throws<InvalidDataException>(() =>
            Store(f).Begin(f.PilotId, module, DogmaEffect.TractorBeam, wreck, new HashSet<long> { wreck }, f.Clock.Now)
        );
        var membership = f.Db.Services.GetRequiredService<ICorporationMembership>();
        var application = membership.Apply(f.PilotId, corporation, "Salvage crew");
        application = membership.Update(
            owner.PilotId,
            new(
                application.Id,
                f.PilotId,
                corporation,
                application.Text,
                CorporationApplicationStatus.Offered,
                "Welcome",
                application.AppliedAt
            )
        );
        membership.Update(
            f.PilotId,
            new(
                application.Id,
                f.PilotId,
                corporation,
                application.Text,
                CorporationApplicationStatus.Joined,
                "Accepted",
                application.AppliedAt
            )
        );
        var activation = Store(f).Begin(f.PilotId, module, DogmaEffect.TractorBeam, wreck, new HashSet<long> { wreck }, f.Clock.Now);
        long until = f.Clock.Now + TimeSpan.TicksPerSecond;
        Store(f).AdvanceTractor(activation.Cycle, until);
        double x = f.Db.Store.FindPilot(1000001)!.Position!.X;
        if (live is not null)
        {
            Assert.Equal(x + 9500, live.Tractors.Position(wreck)!.X, 5);
        }
        else
        {
            using WorldDbContext saved = new(f.Db.Path);
            Assert.Equal(x + 9500, saved.Set<WorldWreck>().Find(wreck)!.X, 5);
        }
        membership.Remove(f.PilotId, f.PilotId);
        using (var metrics = SessionPerformance.Begin("revoked corporation tractor", _ => { }, force: true)!)
        {
            live?.Tractors.Advance(until + TimeSpan.TicksPerSecond);
            Assert.Throws<InvalidDataException>(() => Store(f).AdvanceTractor(activation.Cycle, until + TimeSpan.TicksPerSecond));
            if (resident)
            {
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
                Assert.Equal(x + 9500, live!.Tractors.Position(wreck)!.X, 5);
            }
        }
        using WorldDbContext final = new(f.Db.Path);
        Assert.Equal(owner.PilotId, final.Set<WorldWreck>().Find(wreck)!.OwnerId);
        Assert.False(final.Set<WorldWreck>().Find(wreck)!.Abandoned);
    }
}
