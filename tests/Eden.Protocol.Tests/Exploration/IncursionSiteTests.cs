using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Data.World.Loot;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Exploration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Exploration;

public sealed class IncursionSiteTests
{
    [Fact]
    public void ScopedRetirementInvalidatesWarmedNpcViewsInAnotherVanguardSystem()
    {
        TestClock clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        clock.UtcNow = DateTimeOffset.FromFileTime(IncursionRules.OpensAt(IncursionRules.TaleId(clock.Now))).ToUniversalTime();
        using TestWorld fixture = new(clock: clock);
        var database = new WorldDatabase(fixture.Path);
        PassiveCatalogFixture.LoadIncursions(database.CatalogPath);
        using LiveWorld live = new(database, clock, _ => { });
        IncursionStore campaigns = new(database);
        CombatAnomalyStore sites = new(database);
        var focus = Assert.Single(campaigns.Read(clock.Now));
        var systems = focus.Systems.Where(s => s.SceneType == IncursionScene.Vanguard).Take(2).ToArray();
        var warmed = systems
            .Select(system =>
            {
                var contact = Assert.Single(sites.Incursions(system.SystemId, clock.Now));
                sites.Arrive(system.SystemId, contact.Position + IncursionSiteCatalog.CombatOffset, clock.Now);
                var npc = Npcs(live, contact.Id)[0];
                var combat = live.CombatForSystem(system.SystemId)!;
                Assert.True(combat.RattingNpc(npc.Id, clock.Now)?.Available);
                return (Npc: npc.Id, Combat: combat);
            })
            .ToArray();

        using (var action = live.Economy.Begin(new(systems: [systems[0].SystemId])))
        {
            CombatAnomalyStore.RetireIncursion(action, focus.TaleId, clock.Now);
            action.Commit();
        }

        Assert.All(warmed, view => Assert.Null(view.Combat.RattingNpc(view.Npc, clock.Now)));
    }

    [Fact]
    public async Task VanguardGateAndFourWavesAdvanceAcrossCheckpointAndRetireAtExpiry()
    {
        TestClock clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        clock.UtcNow = DateTimeOffset.FromFileTime(IncursionRules.OpensAt(IncursionRules.TaleId(clock.Now))).ToUniversalTime();
        using TestWorld fixture = new(clock: clock);
        var database = new WorldDatabase(fixture.Path);
        PassiveCatalogFixture.LoadIncursions(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = fixture.Store.FindPilot(1000001)!;
        CombatAnomalyStore sites = new(database);
        IncursionStore campaigns = new(database);
        CombatAnomaly first;
        IncursionFocus focus;
        MissionGate gate;
        int system;

        using (LiveWorld live = new(database, clock, _ => { }))
        {
            focus = Assert.Single(campaigns.Read(clock.Now));
            system = focus.Systems.First(s => s.SceneType == IncursionScene.Vanguard).SystemId;
            Assert.Empty(sites.Incursions(focus.StagingSystemId, clock.Now));
            first = Assert.Single(sites.Incursions(system, clock.Now));
            Assert.DoesNotContain(sites.List(system, clock.Now), s => s.Id == first.Id);
            Assert.Equal(IncursionSiteCatalog.NationCommanderOutpost, first.DungeonId);
            var beacon = Assert.Single(first.Objects, o => o.IncursionBeacon is not null);
            var slim = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(new StaticSlimItem(beacon))));
            var fields = BlueRead.Fields(slim.State);
            Assert.Equal(61L, fields["archetypeID"]);
            Assert.Equal(5166L, fields["dungeonID"]);
            Assert.Equal(114106L, fields["dungeonNameID"]);
            Assert.Equal(500019L, fields["ownerID"]);
            Assert.Equal(first.Id, beacon.Id);

            var entrance = Assert.Single(first.Objects, o => o.GroupId == MissionGateRules.AccelerationGateGroup);
            fixture.Store.SetPosition(pilot.ShipId, first.Position - new SpacePosition(1e9, 0, 0), system);
            AssertNativeWarp("item", first.Id);
            AssertNativeWarp("scan", first.TargetId);
            fixture.Store.SetPosition(pilot.ShipId, first.Position, system);
            sites.Arrive(system, first.Position, clock.Now);
            Assert.Empty(Npcs(live, first.Id));
            Assert.Throws<InvalidDataException>(() => sites.Gate(pilot.PilotId, entrance.Id, clock.Now));
            fixture.Store.SetPosition(pilot.ShipId, entrance.Position, system);
            gate = sites.Gate(pilot.PilotId, entrance.Id, clock.Now);
            Assert.Equal(first.Position + IncursionSiteCatalog.CombatOffset, gate.Destination);
            Assert.Empty(sites.ActivateGate(pilot.PilotId, gate.Id, gate.Destination, clock.Now).Items);
            var distant = first.Position - new SpacePosition(1e9, 0, 0);
            Assert.Equal(first.Position, sites.WarpDestination(system, distant, gate.Destination, clock.Now));
            Assert.Throws<InvalidDataException>(() => sites.WarpDestination(system, first.Position, gate.Destination, clock.Now));

            fixture.Store.SetPosition(pilot.ShipId, gate.Destination, system);
            sites.Arrive(system, gate.Destination, clock.Now);
            var wave = Npcs(live, first.Id);
            Assert.Contains(wave, npc => npc.TypeId == 2859); // Romi Thalamus
            Assert.All(wave, n => Assert.InRange(new SpacePosition(n.X, n.Y, n.Z).DistanceTo(gate.Destination), 15_000, 60_000));
            Assert.True(Hit(live, catalog, wave[0].Id, pilot.PilotId, clock.Now).Destroyed);
            Assert.Equal(0, Wave(live, first.Id));
            await live.FlushAsync();
        }

        using LiveWorld reopened = new(database, clock, _ => { });
        sites = new(database);
        Assert.Equal(first.Id, Assert.Single(sites.Incursions(system, clock.Now)).Id);
        Assert.Equal(0, Wave(reopened, first.Id));
        for (int index = 0; index < 4; index++)
        {
            var wave = Npcs(reopened, first.Id);
            Assert.NotEmpty(wave);
            Assert.Equal(index == 2, wave.Any(n => n.TypeId == 3492)); // Sansha's Nation Commander
            foreach (var npc in wave)
            {
                var hit = Hit(reopened, catalog, npc.Id, pilot.PilotId, clock.Now);
                Assert.True(hit.Destroyed);
                Assert.Null(hit.Reward);
            }
        }
        Assert.Empty(Npcs(reopened, first.Id));
        Assert.Empty(sites.Incursions(system, clock.Now));
        Assert.Empty(reopened.Economy.Inspect(w => w.Filter<SavedCombatDamage>(_ => true).ToArray()));
        Assert.Empty(reopened.Economy.Inspect(w => w.Filter<NpcBounty>(_ => true).ToArray()));
        Assert.Empty(reopened.Economy.Inspect(w => w.Filter<WorldWreck>(_ => true).ToArray()));

        long due = checked(clock.Now + CombatAnomalyRules.ReplacementSeconds * TimeSpan.TicksPerSecond);
        Assert.Empty(sites.Incursions(system, due - 1));
        var replacement = Assert.Single(sites.Incursions(system, due));
        Assert.NotEqual(first.Id, replacement.Id);
        sites.Arrive(system, replacement.Position + IncursionSiteCatalog.CombatOffset, due);
        long expired = Npcs(reopened, replacement.Id)[0].Id;
        var combat = reopened.CombatForSystem(system)!;
        Assert.NotNull(combat.RattingNpc(expired, focus.EndsAt - 1));
        Assert.Null(combat.RattingNpc(expired, focus.EndsAt));
        Assert.Equal(0, Hit(reopened, catalog, expired, pilot.PilotId, focus.EndsAt).Applied);
        Assert.Empty(sites.Incursions(system, focus.EndsAt));
        campaigns.Advance(focus.EndsAt);
        Assert.Empty(Npcs(reopened, replacement.Id));

        void AssertNativeWarp(string subject, object target)
        {
            var current = fixture.Store.FindPilot(1000001)!;
            using PilotCallContext context = new(
                1000001,
                current.Account,
                [current],
                p => new(fixture.Store, p, clock, new SharedSpace(), []),
                42,
                SignedSettings.Empty
            );
            context.Select(current);
            context.Flight.Snapshot();
            using var scope = fixture.Services.CreateScope();
            var flight = scope.ServiceProvider.GetRequiredService<FlightCallHandler>();
            Assert.IsType<PilotCallResult.Success>(flight.Call(context, "beyonce", "CmdWarpToStuff", [subject, target]));
        }
    }

    private static WorldNpc[] Npcs(LiveWorld world, long site) =>
        world.Economy.Inspect(w =>
            w.Filter<WorldNpc>(n => n.EncounterId == site && n.DestroyedAt == null && n.RetiredAt == null).ToArray()
        );

    private static int Wave(LiveWorld world, long site) => world.Economy.Inspect(w => w.Set<WorldCombatAnomaly>().Find(site)!.Wave);

    private static CombatDamageSettlement Hit(LiveWorld world, CatalogDbContext catalog, long npcId, long pilotId, long now)
    {
        using var action = world.Economy.Begin(new(pilots: [pilotId]));
        var npc = action.Set<WorldNpc>().Find(npcId)!;
        var pilot = action.Pilots.Find(pilotId)!;
        var attributes = new NpcProfileCatalog().Read(catalog, npc.TypeId).Attributes;
        var hit = NpcDamagePersistence.Apply(action, catalog, npc, pilot, pilot.ActiveShipId, attributes, new(1_000_000, 0, 0, 0), now);
        action.Commit();
        return hit;
    }
}
