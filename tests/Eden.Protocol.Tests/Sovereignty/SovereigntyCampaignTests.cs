using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyCampaignTests
{
    [Fact]
    public async Task PaidAttackReinforcesAndNineAttackerNodesDestroyWithoutTransferringClaim()
    {
        using SovereigntyCaptureFixture fixture = new();
        long defender = fixture.Fit();
        long attacker = fixture.Fit(pilot: SovereigntyCaptureFixture.OtherPilotId);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 100);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 100, pilot: SovereigntyCaptureFixture.OtherPilotId);
        long hub = Claim(fixture, defender);
        var original = fixture.Claims.Read(SovereigntyCaptureFixture.SystemId)!;
        Reinforce(fixture, attacker, hub);
        var conflict = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!;
        Assert.Equal(SovereigntyPhase.Reinforced, conflict.Phase);
        Assert.Equal(original, fixture.Claims.Read(SovereigntyCaptureFixture.SystemId));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(conflict.CampaignAt!.Value - fixture.Clock.Now));
        fixture.Entosis.AdvanceAll();
        Assert.Equal(5, Nodes(fixture).Length);
        for (int captured = 0; captured < 9; captured++)
        {
            var node = Nodes(fixture).First();
            CaptureNode(fixture, attacker, node, SovereigntyCaptureFixture.OtherPilotId);
            if (captured < 8)
            {
                Assert.Equal(60 - (captured + 1) * 7, fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.DefenderPoints);
                Assert.Equal(original, fixture.Claims.Read(SovereigntyCaptureFixture.SystemId));
            }
        }
        Assert.Null(fixture.Claims.Read(SovereigntyCaptureFixture.SystemId));
        Assert.Null(fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId));
        Assert.Empty(Nodes(fixture));
        fixture.Live.Economy.Inspect(world =>
        {
            Assert.Equal(StructureState.Destroyed, world.Set<SavedStructure>().Find(hub)!.State);
            Assert.Null(world.Set<SavedSovereigntyConflict>().Find(SovereigntyCaptureFixture.SystemId));
            return true;
        });
        long replacement = fixture.Deploy();
        Assert.NotEqual(hub, replacement);
        await fixture.Live.FlushAsync();
        fixture.World.Services.Dispose();
        using var reopened = Eden.Server.ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<IPilotEntosis>();
        Assert.Equal(replacement, restored.Hub(SovereigntyCaptureFixture.SystemId)!.HubId);
        Assert.Null(reopened.GetRequiredService<ISovereignty>().Read(SovereigntyCaptureFixture.SystemId));
        var live = reopened.GetRequiredService<Eden.World.Runtime.LiveWorld>();
        Assert.Empty(live.Economy.Inspect(world => world.Set<SavedSovereigntyNode>().ToArray()));
        Assert.Equal(StructureState.Destroyed, live.Economy.Inspect(world => world.Set<SavedStructure>().Find(hub)!.State));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(72)]
    public void UnattendedCampaignDefendsAndKeepsOriginalClaimUntilNextDaysWindow(int hours)
    {
        using SovereigntyCaptureFixture fixture = new();
        long defender = fixture.Fit();
        long attacker = fixture.Fit(pilot: SovereigntyCaptureFixture.OtherPilotId);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 100);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 100, pilot: SovereigntyCaptureFixture.OtherPilotId);
        long hub = Claim(fixture, defender);
        var claim = fixture.Claims.Read(SovereigntyCaptureFixture.SystemId);
        Reinforce(fixture, attacker, hub);
        long at = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.CampaignAt!.Value;
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(at - fixture.Clock.Now) + TimeSpan.FromHours(hours));
        fixture.Entosis.AdvanceAll();
        if (hours == 72)
        {
            var expected = SovereigntyWarfareRules.Window(18, fixture.Clock.Now);
            var immediate = fixture.Live.Economy.Inspect(world =>
            {
                var row = world.Set<SavedSovereigntyConflict>().Find(SovereigntyCaptureFixture.SystemId)!;
                return new VulnerabilityWindow(row.WindowStart, row.WindowEnd);
            });
            Assert.Equal(expected, immediate);
        }
        var result = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!;
        Assert.Equal(SovereigntyPhase.Secure, result.Phase);
        Assert.Equal(100, result.DefenderPoints);
        Assert.Equal(claim, fixture.Claims.Read(SovereigntyCaptureFixture.SystemId));
        Assert.Empty(Nodes(fixture));
        if (hours == 4)
        {
            Assert.True(result.Window.StartsAt > fixture.Clock.Now);
        }
        Assert.True(result.Window.EndsAt > fixture.Clock.Now);
    }

    [Fact]
    public async Task ReinforcementAndPrimeTransitionReopenWithoutRerollOrOfflineScoring()
    {
        using SovereigntyCaptureFixture fixture = new();
        long defender = fixture.Fit();
        long attacker = fixture.Fit(pilot: SovereigntyCaptureFixture.OtherPilotId);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 100);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 100, pilot: SovereigntyCaptureFixture.OtherPilotId);
        long hub = Claim(fixture, defender);
        fixture.Entosis.SetPrimeHour(SovereigntyCaptureFixture.PilotId, 6);
        var prime = fixture.Entosis.PrimeTime(SovereigntyCaptureFixture.PilotId);
        Assert.Equal(18, prime.CurrentHour);
        Assert.Equal(6, prime.NewHour);
        Reinforce(fixture, attacker, hub);
        long at = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.CampaignAt!.Value;
        long attackingShip = fixture.Ship(SovereigntyCaptureFixture.OtherPilotId);
        await fixture.Live.FlushAsync();
        fixture.World.Services.Dispose();
        fixture.Clock.AdvanceWallTime(TimeSpan.FromHours(1));
        using var services = Eden.Server.ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true);
        var entosis = services.GetRequiredService<IPilotEntosis>();
        Assert.Equal(at, entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.CampaignAt);
        Assert.Equal(prime, entosis.PrimeTime(SovereigntyCaptureFixture.PilotId));
        Assert.Empty(entosis.CommandNodes(SovereigntyCaptureFixture.SystemId));
        Assert.Throws<InvalidDataException>(() =>
            entosis.Activate(SovereigntyCaptureFixture.OtherPilotId, attackingShip, attacker, hub, 10, fixture.Clock.Now)
        );
    }

    [Fact]
    public async Task ActiveNodeCheckpointKeepsGenerationAndRestartStopsAttackerScoring()
    {
        using SovereigntyCaptureFixture fixture = new();
        long defender = fixture.Fit();
        long attacker = fixture.Fit(pilot: SovereigntyCaptureFixture.OtherPilotId);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 100);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 100, pilot: SovereigntyCaptureFixture.OtherPilotId);
        long hub = Claim(fixture, defender);
        Reinforce(fixture, attacker, hub);
        long at = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.CampaignAt!.Value;
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(at - fixture.Clock.Now));
        fixture.Entosis.AdvanceAll();
        var node = Nodes(fixture)[0];
        fixture.Position(new(node.X + 1000, node.Y, node.Z), SovereigntyCaptureFixture.OtherPilotId, node.SystemId);
        fixture.Lock(node.Id, SovereigntyCaptureFixture.OtherPilotId);
        long ship = fixture.Ship(SovereigntyCaptureFixture.OtherPilotId);
        fixture.Entosis.Activate(SovereigntyCaptureFixture.OtherPilotId, ship, attacker, node.Id, 10, fixture.Clock.Now);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        fixture.Entosis.Renew(SovereigntyCaptureFixture.OtherPilotId, attacker, fixture.Clock.Now);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(60));
        fixture.Entosis.AdvanceAll();
        double score = Nodes(fixture).Single(row => row.Id == node.Id).Value;
        Assert.True(score < 0);
        using (var captured = Assert.IsType<Eden.World.Runtime.WorldSnapshot>(fixture.Live.CaptureSnapshot()))
        {
            fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(10));
            fixture.Entosis.AdvanceAll();
            captured.Project();
            using WorldDbContext image = new(captured.Database);
            Assert.Equal(score, image.Set<SavedSovereigntyNode>().Find(node.Id)!.Value);
            Assert.Equal(60, image.Set<SavedSovereigntyConflict>().Find(SovereigntyCaptureFixture.SystemId)!.DefenderPoints);
        }
        double saved = Nodes(fixture).Single(row => row.Id == node.Id).Value;
        await fixture.Live.FlushAsync();
        fixture.World.Services.Dispose();
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        using var services = Eden.Server.ServerServices.Build(new(fixture.World.Path), fixture.Clock, liveWorld: true);
        var entosis = services.GetRequiredService<IPilotEntosis>();
        entosis.AdvanceAll();
        var live = services.GetRequiredService<Eden.World.Runtime.LiveWorld>();
        double restored = live.Economy.Inspect(world => world.Set<SavedSovereigntyNode>().Find(node.Id)!.Value);
        Assert.True(restored > saved);
        Assert.True(entosis.TravelLocked(ship, fixture.Clock.Now));
        Assert.Equal(60, entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.DefenderPoints);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(30));
        entosis.AdvanceAll();
        long[] ids = live.Economy.Inspect(world => world.Set<SavedSovereigntyNode>().Select(row => row.Id).ToArray());
        Assert.True(ids.Max() > node.Id);
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void ConcurrentConstellationCampaignsKeepSeparateScoresAndRespectNodeCaps()
    {
        using SovereigntyCaptureFixture fixture = new();
        long defender = fixture.Fit();
        long attacker = fixture.Fit(pilot: SovereigntyCaptureFixture.OtherPilotId);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 100);
        fixture.Grant(SovereigntyCaptureFixture.Strontium, 100, pilot: SovereigntyCaptureFixture.OtherPilotId);
        long hub = Claim(fixture, defender);
        Reinforce(fixture, attacker, hub);
        long at = fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.CampaignAt!.Value;
        using (var action = fixture.Live.Economy.Begin(Eden.World.Storage.TransactionParticipants.None))
        {
            using Eden.World.Catalog.CatalogDbContext catalog = new(new WorldDatabase(fixture.World.Path).CatalogPath);
            foreach (
                int system in catalog
                    .Definitions.Systems.Values.Where(row =>
                        row.ConstellationId == 20000583 && row.Id != SovereigntyCaptureFixture.SystemId
                    )
                    .Select(row => row.Id)
            )
            {
                long id = Eden.World.Inventory.InventoryPersistence.NextId(action);
                action.Add(
                    new SavedStructure
                    {
                        Id = id,
                        TypeId = EntosisRules.HubTypeId,
                        CorporationId = fixture.CorporationId,
                        SystemId = system,
                        State = StructureState.ShieldVulnerable,
                    }
                );
                action.Add(
                    new SavedSovereigntyHub
                    {
                        SystemId = system,
                        HubId = id,
                        CaptureDuration = TimeSpan.FromSeconds(595).Ticks,
                        UpdatedAt = fixture.Clock.Now,
                    }
                );
                action.Add(
                    new SavedSovereigntyClaim
                    {
                        SystemId = system,
                        HubId = id,
                        CorporationId = fixture.CorporationId,
                        AllianceId = fixture.AllianceId,
                        ClaimedAt = fixture.Clock.Now,
                    }
                );
                action.Add(
                    new SavedSovereigntyConflict
                    {
                        SystemId = system,
                        HubId = id,
                        ConstellationId = 20000583,
                        DefenderAllianceId = fixture.AllianceId,
                        Phase = SovereigntyPhase.Reinforced,
                        DefenderPoints = 60,
                        CampaignAt = at,
                        UpdatedAt = fixture.Clock.Now,
                    }
                );
            }
            action.Commit();
        }
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(at - fixture.Clock.Now));
        fixture.Entosis.AdvanceAll();
        var nodes = Nodes(fixture);
        Assert.Equal(20, nodes.Length);
        Assert.All(nodes.GroupBy(row => row.SystemId), group => Assert.True(group.Count() <= 5));
        var chosen = nodes[0];
        CaptureNode(fixture, attacker, chosen, SovereigntyCaptureFixture.OtherPilotId);
        fixture.Live.Economy.Inspect(world =>
        {
            Assert.Equal(53, world.Set<SavedSovereigntyConflict>().Find(chosen.SourceSystemId)!.DefenderPoints);
            Assert.All(
                world.Set<SavedSovereigntyConflict>().Where(row => row.SystemId != chosen.SourceSystemId),
                row => Assert.Equal(60, row.DefenderPoints)
            );
            return true;
        });
        Assert.Equal(20, Nodes(fixture).Length);
    }

    internal static long Claim(SovereigntyCaptureFixture fixture, long module)
    {
        long hub = fixture.Deploy();
        fixture.Lock(hub);
        fixture.Entosis.Activate(SovereigntyCaptureFixture.PilotId, fixture.Ship(), module, hub, 10, fixture.Clock.Now);
        for (int cycle = 0; cycle < 6; cycle++)
        {
            fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
            fixture.Entosis.Renew(SovereigntyCaptureFixture.PilotId, module, fixture.Clock.Now);
        }
        Assert.NotNull(fixture.Claims.Read(SovereigntyCaptureFixture.SystemId));
        return hub;
    }

    internal static void Reinforce(SovereigntyCaptureFixture fixture, long module, long hub)
    {
        fixture.Position(new(2_000_070_000, 0, 0), SovereigntyCaptureFixture.OtherPilotId);
        fixture.Lock(hub, SovereigntyCaptureFixture.OtherPilotId);
        fixture.Entosis.Activate(
            SovereigntyCaptureFixture.OtherPilotId,
            fixture.Ship(SovereigntyCaptureFixture.OtherPilotId),
            module,
            hub,
            10,
            fixture.Clock.Now
        );
        for (int cycle = 0; cycle < 6; cycle++)
        {
            fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
            fixture.Entosis.Renew(SovereigntyCaptureFixture.OtherPilotId, module, fixture.Clock.Now);
        }
        Assert.Equal(SovereigntyPhase.Reinforced, fixture.Entosis.Hub(SovereigntyCaptureFixture.SystemId)!.Conflict!.Phase);
    }

    internal static SavedSovereigntyNode[] Nodes(SovereigntyCaptureFixture fixture) =>
        fixture.Live.Economy.Inspect(world => world.Set<SavedSovereigntyNode>().OrderBy(row => row.Id).ToArray());

    internal static void CaptureNode(SovereigntyCaptureFixture fixture, long module, SavedSovereigntyNode node, long pilot)
    {
        // A completed node's last paid cycle remains reserved before the next target.
        var previous = fixture.Entosis.Cycles(pilot).FirstOrDefault(row => row.ModuleId == module);
        if (previous is not null && previous.EndsAt > fixture.Clock.Now)
        {
            fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(previous.EndsAt - fixture.Clock.Now));
        }
        fixture.Entosis.Renew(pilot, module, fixture.Clock.Now);
        fixture.Position(new(node.X + 1000, node.Y, node.Z), pilot, node.SystemId);
        fixture.Lock(node.Id, pilot);
        fixture.Entosis.Activate(pilot, fixture.Ship(pilot), module, node.Id, 10, fixture.Clock.Now);
        for (int cycle = 0; cycle < 8 && Nodes(fixture).Any(row => row.Id == node.Id); cycle++)
        {
            fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
            fixture.Entosis.Renew(pilot, module, fixture.Clock.Now);
        }
        Assert.DoesNotContain(Nodes(fixture), row => row.Id == node.Id);
    }
}
