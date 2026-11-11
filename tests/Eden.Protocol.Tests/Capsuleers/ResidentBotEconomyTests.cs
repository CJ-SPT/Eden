using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Mining;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class ResidentBotEconomyTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaidCollectionUsesNoSqlAndRollbackPreservesItsDeadline(bool commit)
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        MinerObservation? paid = null;
        for (int action = 0; action < 1000; action++)
        {
            paid = CapsuleerTestDriver.Miner(host, fixture.Clock);
            if (paid.Work.Activity == MinerActivity.Mining)
            {
                break;
            }
        }
        Assert.Equal(MinerActivity.Mining, paid!.Work.Activity);
        var live = host.GetRequiredService<LiveWorld>();
        var participants = new TransactionParticipants(pilots: [paid.Pilot.PilotId], ships: [paid.Pilot.ShipId]);
        long before;
        long ready;
        using (var read = live.Economy.Begin(participants))
        {
            before = read.Set<WorldAsteroid>().Find(paid.Work.TargetId)!.Quantity;
            ready = read.Set<ModuleCooldown>().Find(paid.Work.ModuleId)!.ReadyAt;
        }
        fixture.Clock.Seconds += 180;
        long remaining;
        List<string> commands = [];
        using (var metrics = SessionPerformance.Begin("resident paid mining", _ => { }, force: true, command: commands.Add)!)
        {
            using var action = live.Economy.Begin(participants);
            using var catalog = new Eden.World.Catalog.CatalogDbContext(fixture.Database.CatalogPath);
            var result = PilotMiningStore.CollectRemote(
                action,
                catalog,
                paid.Pilot.PilotId,
                paid.Work.ModuleId,
                paid.Work.TargetId,
                TimeSpan.FromSeconds(180).Ticks,
                new(.5, .5, .5, .5, .5),
                fixture.Clock.Now
            );
            remaining = action.Set<WorldAsteroid>().Find(paid.Work.TargetId)!.Quantity;
            Assert.True(remaining < before);
            Assert.True(result.Cycles > 0);
            Assert.Null(action.Set<ModuleCooldown>().Find(paid.Work.ModuleId));
            Assert.Throws<InvalidDataException>(() =>
                PilotMiningStore.CollectRemote(
                    action,
                    catalog,
                    paid.Pilot.PilotId,
                    paid.Work.ModuleId,
                    paid.Work.TargetId,
                    0,
                    new(.5, .5, .5, .5, .5),
                    fixture.Clock.Now
                )
            );
            if (commit)
            {
                action.Commit();
            }
            Assert.True(metrics.Commands == 0, string.Join(Environment.NewLine, commands));
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using (var read = live.Economy.Begin(participants))
        {
            Assert.Equal(commit ? remaining : before, read.Set<WorldAsteroid>().Find(paid.Work.TargetId)!.Quantity);
            Assert.Equal(commit ? null : (long?)ready, read.Set<ModuleCooldown>().Find(paid.Work.ModuleId)?.ReadyAt);
        }
        using var captured = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        captured.Project();
        using var saved = new WorldDbContext(captured.Database);
        Assert.Equal(commit ? remaining : before, saved.Set<WorldAsteroid>().Find(paid.Work.TargetId)!.Quantity);
        Assert.Equal(commit ? null : (long?)ready, saved.Set<ModuleCooldown>().Find(paid.Work.ModuleId)?.ReadyAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemplateRefreshCommitsPilotHullAndIdentityWithoutCloningUnrelatedPortraits(bool commit)
    {
        const int source = 30000141;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        new CapsuleerProvisioner(fixture.Database, fixture.Clock).EnsureSystem(source);
        long pilotId;
        long shipId;
        string original;
        using (WorldDbContext setup = new(fixture.Database.Path))
        {
            var agent = setup.Set<SavedCapsuleerAgent>().Single(a => a.Career == CapsuleerCareer.Hauling);
            pilotId = agent.PilotId;
            shipId = agent.FounderShipId;
            setup.Database.ExecuteSql($"DELETE FROM WorldPosition WHERE ShipId={shipId}");
            var pilot = setup.Pilots.Find(pilotId)!;
            original = pilot.Name;
            pilot.Appearance = [1, 2, 3];
            pilot.Portrait = [4, 5];
            byte[] payload = new byte[256 * 1024];
            const long extraPilot = 95000000;
            const long extraShip = 350000000000;
            for (int index = 0; index < 64; index++)
            {
                setup.Add(new InventoryOwner { Id = extraPilot + index });
                setup.Add(
                    new Ship
                    {
                        Id = extraShip + index,
                        OwnerId = extraPilot + index,
                        TypeId = 648,
                        StationId = 60015157,
                        Name = $"Unrelated ship {index}",
                    }
                );
                setup.Add(
                    new Pilot
                    {
                        Id = extraPilot + index,
                        AccountId = pilot.AccountId,
                        ActiveShipId = extraShip + index,
                        Name = $"Unrelated pilot {index}",
                        CorporationId = pilot.CorporationId,
                        RaceId = pilot.RaceId,
                        BloodlineId = pilot.BloodlineId,
                        SchoolId = pilot.SchoolId,
                        Appearance = payload,
                        Portrait = payload,
                    }
                );
            }
            setup.SaveChanges();
        }
        var profile = TrafficSnapshotTests.Profile() with { HullTypeId = 603 };
        TrafficSnapshotTests.Install(fixture.Database, profile);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        var participants = new TransactionParticipants(pilots: [pilotId], ships: [shipId]);
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        CapsuleerTrafficRoute route = new(60015157, 60003760, [source, 30000142]);
        // Hydrate and warm immutable catalog inputs before measuring the action.
        using (var warm = live.Economy.Begin(participants))
        {
            Assert.True(TrafficProvisioning.Refresh(fixture.Database, warm, catalog, pilotId, route, fixture.Clock.Now));
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        using (var metrics = SessionPerformance.Begin("resident traffic template", _ => { }, force: true)!)
        {
            using var action = live.Economy.Begin(participants);
            int count = action.Pilots.Count();
            Assert.Equal(count, action.Pilots.Select(p => p.Name).ToArray().Length);
            Assert.True(action.Pilots.Any(p => p.Id == pilotId));
            Assert.Equal(1, action.Pilots.Count(p => p.Id == pilotId));
            Assert.Equal(1L, action.Pilots.LongCount(p => p.Id == pilotId));
            Assert.Empty(action.Pilots.Local);
            Assert.Equal(pilotId, action.Pilots.Single(p => p.Id == pilotId).Id);
            Assert.Single(action.Pilots.Local);
            Assert.True(TrafficProvisioning.Refresh(fixture.Database, action, catalog, pilotId, route, fixture.Clock.Now));
            Assert.Single(action.Pilots.Local);
            Assert.Equal(profile.Name, action.Pilots.Find(pilotId)!.Name);
            Assert.True(action.HasPilotName(profile.Name.ToUpperInvariant()));
            Assert.True(action.TrafficCharacterTaken(profile.CharacterId));
            Assert.Equal(count, action.PilotCount);
            if (commit)
            {
                action.Commit();
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        output.WriteLine($"Resident template refresh allocated {allocated:N0} bytes (commit={commit}).");
        Assert.InRange(allocated, 0, 8 * 1024 * 1024);
        using var captured = live.CaptureSnapshot();
        if (commit)
        {
            Assert.NotNull(captured);
            captured.Project();
        }
        else
        {
            Assert.Null(captured);
        }
        using WorldDbContext saved = captured is null ? new(fixture.Database.Path) : new(captured.Database);
        var restored = saved.Pilots.Find(pilotId)!;
        Assert.Equal(commit ? profile.Name : original, restored.Name);
        Assert.Equal(new byte[] { 1, 2, 3 }, restored.Appearance);
        Assert.Equal(new byte[] { 4, 5 }, restored.Portrait);
        Assert.Equal(commit ? profile.HullTypeId : 648, saved.Ships.Find(shipId)!.TypeId);
        Assert.Equal(commit, saved.Set<SavedTrafficIdentity>().Any(i => i.PilotId == pilotId));
    }
}
