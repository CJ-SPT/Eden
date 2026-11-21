using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Mining;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Market;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
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
            var result = PilotMiningStore.Collect(
                action,
                catalog,
                paid.Pilot.PilotId,
                paid.Work.ModuleId,
                paid.Work.TargetId,
                new(1, true, new(.5, .5, .5, .5, .5)),
                fixture.Clock.Now
            );
            remaining = action.Set<WorldAsteroid>().Find(paid.Work.TargetId)!.Quantity;
            Assert.True(remaining < before);
            Assert.True(result.Extracted > 0);
            Assert.Null(action.Set<ModuleCooldown>().Find(paid.Work.ModuleId));
            Assert.Throws<InvalidDataException>(() =>
                PilotMiningStore.Collect(
                    action,
                    catalog,
                    paid.Pilot.PilotId,
                    paid.Work.ModuleId,
                    paid.Work.TargetId,
                    new(1, true, new(.5, .5, .5, .5, .5)),
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

    [Fact]
    public void MiningExpeditionGateTransferPublishesOnlyToItsEndpointSystems()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock, liveWorld: true);
        var pilot = db.Store.FindPilot(1000001)!;
        var gate = db.Objects.Single(body => body.DestinationGateId.HasValue);
        int destination = gate.DestinationSystemId!.Value;
        db.Store.SetPosition(pilot.ShipId, gate.Position);

        var live = db.Services.GetRequiredService<LiveWorld>();
        var database = db.Services.GetRequiredService<WorldDatabase>();
        var market = db.Services.GetRequiredService<MarketChangeSignal>();
        using var owner = live.EnterShared();
        const int unrelated = 30000145;
        Assert.NotEqual(unrelated, pilot.SystemId);
        Assert.NotEqual(unrelated, destination);
        var originBefore = live.PresentationVersion(pilot.SystemId);
        var destinationBefore = live.PresentationVersion(destination);
        var unrelatedBefore = live.PresentationVersion(unrelated);
        CapsuleerJob job = new(pilot.PilotId, 0, 0, 0, CapsuleerAction.GateTransfer);
        MinerCheckpoint checkpoint = new(
            pilot.PilotId,
            new(MinerActivity.Jumping, gate.Id, 0, 0, 0, 0, 0, new NavigationState().Capture(), pilot.ShipId)
        );
        using var systemContext = live.UseSystem(pilot.SystemId);
        using CapsuleerOperation operation = new(database, job, market);
        live.MinerCheckpoints[pilot.PilotId] = checkpoint;
        EconomyTransaction action;
        try
        {
            action = Assert.IsType<EconomyTransaction>(operation.World);
        }
        finally
        {
            live.MinerCheckpoints.Remove(pilot.PilotId);
        }
        var transfer = PilotGateTravelStore.TransferDetailed(
            action,
            operation.Catalog,
            pilot.PilotId,
            pilot.ShipId,
            gate.Id,
            gate.DestinationGateId!.Value,
            clock.Now
        );
        action.Commit();

        Assert.Equal(destination, transfer.System.Id);
        Assert.Equal(destination, live.Economy.Peek<Eden.Data.World.Docking.FlightPosition>(pilot.ShipId)!.SystemId);
        Assert.NotEqual(originBefore, live.PresentationVersion(pilot.SystemId));
        Assert.NotEqual(destinationBefore, live.PresentationVersion(destination));
        Assert.Equal(unrelatedBefore, live.PresentationVersion(unrelated));
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
            FreightTests.SetLegacyCargo(setup, fixture.Database, pilotId, shipId);
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
        if (commit)
        {
            using var edit = live.Economy.Begin(participants);
            var identity = edit.Set<SavedTrafficIdentity>().Find(pilotId)!;
            var renamed = edit.Pilots.Find(pilotId)!;
            renamed.Name = original;
            identity.CharacterId++;
            Assert.False(edit.HasPilotName(profile.Name));
            Assert.True(edit.HasPilotName(original.ToUpperInvariant()));
            Assert.False(edit.TrafficCharacterTaken(profile.CharacterId));
            Assert.True(edit.TrafficCharacterTaken(identity.CharacterId));
            edit.Remove(identity);
            Assert.False(edit.TrafficCharacterTaken(identity.CharacterId));
        }
        using (var read = live.Economy.Begin(participants))
        {
            Assert.Equal(commit, read.HasPilotName(profile.Name));
            Assert.Equal(commit, read.TrafficCharacterTaken(profile.CharacterId));
            Assert.Empty(read.Set<SavedTrafficIdentity>().Local);
        }
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
