using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.Server;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Sovereignty;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Sovereignty.SovereigntyCaptureFixture;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyDissolutionTests
{
    private const int CapitalCandidate = 30003990;
    private const int OtherSystem = 30003991;
    private const int UpgradeType = 81615;
    private const int FuelType = 81143;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllianceDissolutionDropsTerritoryAtDowntimeAndSurvivesReopen(bool lastMemberLeaves)
    {
        using SovereigntyCaptureFixture fixture = new();
        var services = fixture.World.Services;
        var alliances = services.GetRequiredService<IAlliances>();
        var members = services.GetRequiredService<IAllianceMembership>();
        var billing = services.GetRequiredService<IAllianceBilling>();
        var resources = services.GetRequiredService<ISovereigntyResources>();
        long hub = SovereigntyUpgradeTests.Claim(fixture);
        SovereigntyDevelopmentTests.AddClaim(fixture, CapitalCandidate);
        fixture.Entosis.SetCapital(PilotId, CapitalCandidate);
        fixture.Entosis.SetPrimeHour(PilotId, 6);
        long skyhook = SovereigntyUpgradeTests.Deploy(fixture, workforce: true).Id;
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(30));
        SovereigntyUpgradeTests.MoveToHub(fixture, hub);
        resources.Configure(PilotId, hub, [fixture.Grant(UpgradeType)], [new(UpgradeType, true)]);
        resources.AddFuel(PilotId, hub, fixture.Grant(FuelType, 62410), 62410);
        resources.ConfigureWorkforce(PilotId, hub, new(SovereigntyWorkforceMode.Import, Sources: [CapitalCandidate]));

        long attackerModule = fixture.Fit(pilot: OtherPilotId);
        long attackerShip = fixture.Ship(OtherPilotId);
        var otherBill = Assert.Single(billing.Bills(OtherPilotId));
        int otherCorporation = services.GetRequiredService<IPlayerCorporations>().Identity(OtherPilotId).Id;
        SovereigntyClaim otherClaim;
        using (var setup = fixture.Live.Economy.Begin(TransactionParticipants.None))
        {
            setup.Set<SavedCorporation>().Find(otherCorporation)!.BalanceCents = otherBill.AmountCents;
            long otherHub = InventoryPersistence.NextId(setup);
            setup.Add(
                new SavedStructure
                {
                    Id = otherHub,
                    TypeId = EntosisRules.HubTypeId,
                    CorporationId = otherCorporation,
                    SystemId = OtherSystem,
                    State = StructureState.ShieldVulnerable,
                }
            );
            setup.Add(
                new SavedSovereigntyHub
                {
                    SystemId = OtherSystem,
                    HubId = otherHub,
                    CaptureDuration = TimeSpan.FromMinutes(10).Ticks,
                    UpdatedAt = fixture.Clock.Now,
                }
            );
            setup.Add(
                new SavedSovereigntyClaim
                {
                    SystemId = OtherSystem,
                    HubId = otherHub,
                    CorporationId = otherCorporation,
                    AllianceId = fixture.OtherAllianceId,
                    ClaimedAt = fixture.Clock.Now,
                }
            );
            setup.Commit();
            otherClaim = new(OtherSystem, otherHub, otherCorporation, fixture.OtherAllianceId, fixture.Clock.Now);
        }
        billing.Pay(OtherPilotId, otherBill.Id, CorporationWalletRules.MasterKey);
        long boundary = lastMemberLeaves ? fixture.Clock.Now : Assert.Single(billing.Bills(PilotId)).DueAt;
        long closing = AllianceBillingRules.NextDowntime(boundary);
        if (lastMemberLeaves)
        {
            members.Remove(PilotId, fixture.CorporationId);
        }
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(closing - fixture.Clock.Now - 1));
        members.Advance();
        Assert.Null(alliances.Read(fixture.AllianceId)!.ClosedAt);
        Assert.Equal(hub, fixture.Claims.Read(SystemId)!.HubId);
        Assert.NotNull(fixture.Claims.Read(CapitalCandidate));

        long node;
        long cycleEnds = closing + TimeSpan.FromMinutes(2).Ticks;
        using (var setup = fixture.Live.Economy.Begin(TransactionParticipants.None))
        {
            // Place a campaign node in another system to verify cleanup follows its source claim.
            var conflict = setup.Set<SavedSovereigntyConflict>().Find(SystemId)!;
            conflict.Phase = SovereigntyPhase.Campaign;
            conflict.CampaignAt = fixture.Clock.Now;
            conflict.NextSpawnAt = cycleEnds;
            node = InventoryPersistence.NextId(setup);
            setup.Add(
                new SavedSovereigntyNode
                {
                    Id = node,
                    SystemId = OtherSystem,
                    SourceSystemId = SystemId,
                    HubId = hub,
                    CorporationId = fixture.CorporationId,
                    AllianceId = fixture.AllianceId,
                    UpdatedAt = fixture.Clock.Now,
                }
            );
            setup.Add(
                new SavedEntosisCycle
                {
                    ModuleId = attackerModule,
                    TypeId = LinkTwo,
                    PilotId = OtherPilotId,
                    ShipId = attackerShip,
                    HubId = node,
                    SystemId = OtherSystem,
                    CorporationId = otherCorporation,
                    AllianceId = fixture.OtherAllianceId,
                    StartedAt = fixture.Clock.Now,
                    WarmupUntil = fixture.Clock.Now,
                    EndsAt = cycleEnds,
                    SpeedLimit = 100,
                    Contributing = true,
                    Repeats = 2,
                }
            );
            setup.Commit();
        }

        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        members.Advance();
        AssertDissolved(services);
        await fixture.Live.FlushAsync();
        fixture.World.Services.Dispose();
        using var reopened = ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true);
        AssertDissolved(reopened);
        var entosis = reopened.GetRequiredService<IPilotEntosis>();
        Assert.True(entosis.TravelLocked(attackerShip, fixture.Clock.Now));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(cycleEnds - fixture.Clock.Now));
        Assert.Null(entosis.Renew(OtherPilotId, attackerModule, fixture.Clock.Now));
        Assert.Empty(entosis.Cycles(OtherPilotId));

        void AssertDissolved(IServiceProvider host)
        {
            var restoredAlliances = host.GetRequiredService<IAlliances>();
            Assert.Equal(closing, restoredAlliances.Read(fixture.AllianceId)!.ClosedAt);
            Assert.Null(restoredAlliances.Affiliation(fixture.CorporationId));
            var claims = host.GetRequiredService<ISovereignty>();
            Assert.Null(claims.Read(SystemId));
            Assert.Null(claims.Read(CapitalCandidate));
            Assert.Equal(otherClaim, claims.Read(OtherSystem));
            var cycle = Assert.Single(host.GetRequiredService<IPilotEntosis>().Cycles(OtherPilotId));
            Assert.True(cycle.Stopping);
            Assert.Equal(cycleEnds, cycle.EndsAt);
            host.GetRequiredService<LiveWorld>()
                .Economy.Inspect(world =>
                {
                    var structure = world.Set<SavedStructure>().Find(hub)!;
                    Assert.Equal(StructureState.Destroyed, structure.State);
                    Assert.Equal(closing, structure.StateStartedAt);
                    Assert.Null(world.Set<SavedSovereigntyHub>().Find(SystemId));
                    Assert.Null(world.Set<SavedSovereigntyHub>().Find(CapitalCandidate));
                    Assert.Null(world.Set<SavedSovereigntyConflict>().Find(SystemId));
                    Assert.Null(world.Set<SavedSovereigntyNode>().Find(node));
                    Assert.Null(world.Set<SavedSovereigntyResourceHub>().Find(hub));
                    Assert.Null(world.Set<SavedSovereigntyWorkforce>().Find(hub));
                    Assert.Empty(world.Filter<SavedSovereigntyUpgrade>(row => row.HubId == hub));
                    Assert.Empty(world.Filter<SavedSovereigntyFuel>(row => row.HubId == hub));
                    Assert.Empty(world.Filter<SavedSovereigntyWorkforceSource>(row => row.HubId == hub));
                    Assert.NotNull(world.Set<SavedSovereigntySkyhook>().Find(skyhook));
                    Assert.False(world.Set<SavedEntosisCycle>().Find(attackerModule)!.Contributing);
                    Assert.Equal(
                        new SovereigntyCapital(null, null, null),
                        SovereigntyDevelopmentPersistence.Capital(world, fixture.AllianceId)
                    );
                    return true;
                });
        }
    }

    [Fact]
    public async Task StartupReconcilesTerritoryFromAnAlreadyClosedAllianceAndPersistsTheRepair()
    {
        using SovereigntyCaptureFixture fixture = new();
        long hub = SovereigntyUpgradeTests.Claim(fixture);
        fixture.World.Services.GetRequiredService<IAllianceMembership>().Remove(PilotId, fixture.CorporationId);
        long closedAt = fixture.Clock.Now;
        using (var setup = fixture.Live.Economy.Begin(TransactionParticipants.None))
        {
            var saved = setup.Set<SavedAlliance>().Find(fixture.AllianceId)!;
            saved.ClosedAt = closedAt;
            setup.Commit();
        }
        Assert.NotNull(fixture.Claims.Read(SystemId));
        await fixture.Live.FlushAsync();
        fixture.World.Services.Dispose();
        fixture.Clock.AdvanceWallTime(TimeSpan.FromHours(1));
        using (var restored = ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true))
        {
            restored.GetRequiredService<ISovereigntyResources>().AdvanceAll();
            Assert.Null(restored.GetRequiredService<ISovereignty>().Read(SystemId));
            await restored.GetRequiredService<LiveWorld>().FlushAsync();
        }
        using var reopened = ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true);
        Assert.Null(reopened.GetRequiredService<ISovereignty>().Read(SystemId));
        Assert.Equal(closedAt, reopened.GetRequiredService<IAlliances>().Read(fixture.AllianceId)!.ClosedAt);
        reopened
            .GetRequiredService<LiveWorld>()
            .Economy.Inspect(world =>
            {
                var structure = world.Set<SavedStructure>().Find(hub)!;
                Assert.Equal(StructureState.Destroyed, structure.State);
                Assert.Equal(closedAt, structure.StateStartedAt);
                Assert.Null(world.Set<SavedSovereigntyConflict>().Find(SystemId));
                return true;
            });
    }
}
