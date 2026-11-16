using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.Server;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyMigrationTests
{
    [Fact]
    public async Task Version180UpgradePreservesNeutralHubFuelAndPaidReservation()
    {
        using SovereigntyCaptureFixture fixture = new();
        long module = fixture.Fit();
        long fuel = fixture.Grant(SovereigntyCaptureFixture.Strontium, 2);
        long hub = fixture.Deploy();
        fixture.Lock(hub);
        long ship = fixture.Ship();
        var cycle = fixture.Entosis.Activate(SovereigntyCaptureFixture.PilotId, ship, module, hub, 10, fixture.Clock.Now).Cycle;
        await fixture.Live.FlushAsync();
        fixture.World.Services.Dispose();
        using (WorldDbContext old = new(fixture.World.Path))
        {
            old.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldSovereigntyConflict;
                DROP TABLE WorldSovereigntyPrime;
                DROP TABLE WorldSovereigntyNode;
                PRAGMA user_version=180;
                """
            );
        }
        WorldInitialization.Initialize(new(fixture.World.Path));
        using var services = ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true);
        var entosis = services.GetRequiredService<IPilotEntosis>();
        Assert.Equal(hub, entosis.Hub(SovereigntyCaptureFixture.SystemId)!.HubId);
        Assert.Equal(EntosisRules.NeutralCorporationId, entosis.Hub(SovereigntyCaptureFixture.SystemId)!.CorporationId);
        Assert.True(entosis.TravelLocked(ship, fixture.Clock.Now));
        Assert.Equal(cycle.EndsAt, Assert.Single(entosis.Cycles(SovereigntyCaptureFixture.PilotId)).EndsAt);
        var live = services.GetRequiredService<Eden.World.Runtime.LiveWorld>();
        Assert.Equal(1, live.Economy.Inspect(world => world.Set<OwnedItem>().Find(fuel)!.Quantity));
        Assert.Null(services.GetRequiredService<ISovereignty>().Read(SovereigntyCaptureFixture.SystemId));
    }

    [Fact]
    public async Task LegacyGenericHubsBecomeRecoverableWithoutGrantingClaimsOrDuplicatingInventory()
    {
        using SovereigntyCaptureFixture fixture = new(hull: 20183);
        long legacy = StructureRules.FirstStructureId + 50;
        SpacePosition point = new(2_000_000_000, 0, 0);
        fixture.Position(point);
        using (var action = fixture.Live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(
                new SavedStructure
                {
                    Id = legacy,
                    TypeId = EntosisRules.HubTypeId,
                    Name = "Legacy Hub",
                    CorporationId = fixture.CorporationId,
                    SystemId = SovereigntyCaptureFixture.SystemId,
                    X = point.X,
                    State = StructureState.ShieldVulnerable,
                    StateStartedAt = fixture.Clock.Now,
                    DeployedAt = fixture.Clock.Now,
                }
            );
            action.Commit();
        }
        await fixture.Live.FlushAsync();
        fixture.World.Services.Dispose();
        using (WorldDbContext version = new(fixture.World.Path))
        {
            version.Database.ExecuteSqlRaw("PRAGMA user_version=179;");
        }
        WorldInitialization.Initialize(new(fixture.World.Path));
        using var reopened = ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true);
        var structures = reopened.GetRequiredService<IPilotStructures>();
        var old = structures.Read(SovereigntyCaptureFixture.PilotId, legacy)!;
        Assert.Equal(StructureState.Unanchored, old.State);
        Assert.Equal(fixture.CorporationId, old.CorporationId);
        Assert.Null(reopened.GetRequiredService<IPilotEntosis>().Hub(SovereigntyCaptureFixture.SystemId));
        Assert.Null(reopened.GetRequiredService<ISovereignty>().Read(SovereigntyCaptureFixture.SystemId));
        structures.Recover(SovereigntyCaptureFixture.PilotId, legacy);
        Assert.Throws<InvalidDataException>(() => structures.Recover(SovereigntyCaptureFixture.PilotId, legacy));
        var live = reopened.GetRequiredService<Eden.World.Runtime.LiveWorld>();
        Assert.Equal(
            1,
            live.Economy.Inspect(world => world.Filter<OwnedItem>(row => row.TypeId == EntosisRules.HubTypeId).Sum(row => row.Quantity))
        );
        await live.FlushAsync();
    }
}
