using Eden.Data.World.Alliances;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Sovereignty;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Sovereignty;
using Eden.Server;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Sovereignty.SovereigntyCaptureFixture;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyClaimCustodyTests
{
    private const int ObserverSystem = 30000142;
    private const int UpgradeType = 81615;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DepartureTransfersHubCustodyWhenAnExecutorIsAvailable(bool electAfterRestart)
    {
        using SovereigntyCaptureFixture fixture = new();
        var services = fixture.World.Services;
        var alliances = services.GetRequiredService<IAlliances>();
        var members = services.GetRequiredService<IAllianceMembership>();
        var resources = services.GetRequiredService<ISovereigntyResources>();
        int replacement = JoinAlliance(fixture);
        long hub = SovereigntyUpgradeTests.Claim(fixture);
        var claim = fixture.Claims.Read(SystemId)!;
        SovereigntyUpgradeTests.MoveToHub(fixture, hub);
        resources.Configure(PilotId, hub, [fixture.Grant(UpgradeType)], [new(UpgradeType, true)]);
        Assert.True(resources.CanManageHub(PilotId, hub));
        Assert.False(resources.CanManageHub(OtherPilotId, hub));

        if (!electAfterRestart)
        {
            members.DeclareSupport(PilotId, replacement);
            members.DeclareSupport(OtherPilotId, replacement);
            Assert.Equal(replacement, alliances.Read(fixture.AllianceId)!.ExecutorCorporationId);
        }
        long node;
        using (var world = fixture.Live.Economy.Begin(TransactionParticipants.None))
        {
            var conflict = world.Set<SavedSovereigntyConflict>().Find(SystemId)!;
            conflict.Phase = SovereigntyPhase.Campaign;
            conflict.CampaignAt = fixture.Clock.Now;
            conflict.NextSpawnAt = fixture.Clock.Now + TimeSpan.FromMinutes(5).Ticks;
            node = InventoryPersistence.NextId(world);
            world.Add(
                new SavedSovereigntyNode
                {
                    Id = node,
                    SystemId = SystemId,
                    SourceSystemId = SystemId,
                    HubId = hub,
                    CorporationId = fixture.CorporationId,
                    AllianceId = fixture.AllianceId,
                    UpdatedAt = fixture.Clock.Now,
                }
            );
            world.Commit();
        }
        fixture.Position(new(1_000_000, 0, 0), system: ObserverSystem);
        using (var scope = services.CreateScope())
        {
            var pilot = fixture.World.Store.FindPilot(1000001)!;
            using PilotCallContext context = new(
                1000001,
                pilot.Account,
                [pilot],
                selected => scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create(selected),
                1,
                SignedSettings.Empty
            );
            context.Select(pilot);
            var monitor = scope.ServiceProvider.GetRequiredService<SovereigntyMonitor>();
            monitor.Advance(context);
            Assert.Empty(context.Notifications);

            long revision = fixture.Entosis.ViewRevision;
            members.Remove(PilotId, fixture.CorporationId);
            Assert.False(resources.CanManageHub(PilotId, hub));
            if (electAfterRestart)
            {
                Assert.Null(alliances.Read(fixture.AllianceId)!.ExecutorCorporationId);
                Assert.Equal(claim, fixture.Claims.Read(SystemId));
                Assert.False(resources.CanManageHub(OtherPilotId, hub));
            }
            else
            {
                AssertTransferred(services);
                Assert.True(fixture.Entosis.ViewRevision > revision);
                monitor.Advance(context);
                var updated = Assert.Single(context.Notifications.OfType<SovereigntyStatusChanged>());
                Assert.Equal(claim with { CorporationId = replacement }, updated.Claim);
            }
        }
        await fixture.Live.FlushAsync();
        services.Dispose();
        using var restored = ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true);
        if (electAfterRestart)
        {
            restored.GetRequiredService<IAllianceMembership>().DeclareSupport(OtherPilotId, replacement);
        }
        AssertTransferred(restored);

        void AssertTransferred(IServiceProvider host)
        {
            Assert.Equal(claim with { CorporationId = replacement }, host.GetRequiredService<ISovereignty>().Read(SystemId));
            Assert.True(host.GetRequiredService<ISovereigntyResources>().CanManageHub(OtherPilotId, hub));
            host.GetRequiredService<LiveWorld>()
                .Economy.Inspect(world =>
                {
                    Assert.Equal(replacement, world.Set<SavedStructure>().Find(hub)!.CorporationId);
                    Assert.Equal(replacement, world.Set<SavedSovereigntyNode>().Find(node)!.CorporationId);
                    Assert.Equal(SovereigntyPhase.Campaign, world.Set<SavedSovereigntyConflict>().Find(SystemId)!.Phase);
                    Assert.Contains(
                        world.Filter<SavedSovereigntyUpgrade>(upgrade => upgrade.HubId == hub),
                        row => row.TypeId == UpgradeType
                    );
                    return true;
                });
        }
    }

    [Fact]
    public async Task StartupRepairsHubCustodyFromAnOlderCheckpoint()
    {
        using SovereigntyCaptureFixture fixture = new();
        int replacement = JoinAlliance(fixture);
        long hub = SovereigntyUpgradeTests.Claim(fixture);
        var claim = fixture.Claims.Read(SystemId)!;
        using (var world = fixture.Live.Economy.Begin(TransactionParticipants.None))
        {
            var alliance = world.Set<SavedAlliance>().Find(fixture.AllianceId)!;
            alliance.ExecutorCorporationId = replacement;
            world.Remove(world.Set<SavedAllianceMember>().Find(fixture.CorporationId)!);
            world.Commit();
        }
        await fixture.Live.FlushAsync();
        fixture.World.Services.Dispose();
        using var restored = ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true);
        var resources = restored.GetRequiredService<ISovereigntyResources>();
        Assert.False(resources.CanManageHub(OtherPilotId, hub));
        resources.AdvanceAll();
        Assert.True(resources.CanManageHub(OtherPilotId, hub));
        Assert.Equal(claim with { CorporationId = replacement }, restored.GetRequiredService<ISovereignty>().Read(SystemId));
    }

    private static int JoinAlliance(SovereigntyCaptureFixture fixture)
    {
        int replacement = fixture.World.Store.FindPilot(1000002)!.CorpId;
        var members = fixture.World.Services.GetRequiredService<IAllianceMembership>();
        members.Remove(OtherPilotId, replacement);
        members.Apply(OtherPilotId, fixture.AllianceId, "Join");
        members.Decide(PilotId, replacement, "Join", AllianceApplicationStatus.Accepted);
        fixture.Clock.AdvanceWallTime(AllianceMembershipRules.AdmissionDelay);
        members.Advance();
        return replacement;
    }
}
