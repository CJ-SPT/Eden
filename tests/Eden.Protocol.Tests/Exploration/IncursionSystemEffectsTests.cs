using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Tests.Combat;
using Eden.Protocol.Tests.Fixtures;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Exploration;

public sealed class IncursionSystemEffectsTests
{
    [Theory]
    [InlineData(IncursionScene.Staging, null, 0)]
    [InlineData(IncursionScene.Vanguard, 3069, 0.1)]
    [InlineData(IncursionScene.Assault, 3493, 0.25)]
    [InlineData(IncursionScene.Headquarters, 3494, 0.5)]
    public void PinnedSystemProfilesScaleOnlyCombatWithInfluence(IncursionScene scene, int? typeId, double fullPenalty)
    {
        IncursionSystem system = new(30000144, scene);
        IncursionFocus focus = new(100, system.SystemId, 20000020, 10, 20, 0.5, [system]);
        var effect = IncursionSystemEffectRules.Create(focus, system);

        Assert.Equal(typeId, effect.SourceTypeId);
        Assert.Equal(1 - fullPenalty * focus.Influence, effect.WeaponDamageMultiplier, 10);
        Assert.Equal(1 + fullPenalty * focus.Influence, effect.ResonanceMultiplier, 10);
        Assert.Equal(0.5m, effect.BountyMultiplier);
        Assert.True(effect.JamsStandardCynos);

        var cleared = IncursionSystemEffectRules.Create(focus with { Influence = 0 }, system);
        Assert.Equal(1, cleared.WeaponDamageMultiplier);
        Assert.Equal(1, cleared.ResonanceMultiplier);
        Assert.Equal(0.5m, cleared.BountyMultiplier);
        Assert.True(cleared.JamsStandardCynos);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WarmTurretAndResonanceFollowInfluenceAndExactDeadline(bool fast)
    {
        using NpcCombatTests.Fixture fixture = new(liveWorld: true, simulationObjects: fast, useTestClock: true);
        long pilotId = fixture.Pilot.PilotId;
        int systemId = fixture.Npc.SystemId;
        var live = LiveWorld.Find(fixture.Db.Path)!;
        var effects = fixture.Db.Services.GetRequiredService<IIncursionSystemEffects>();
        var fitting = fixture.Db.Services.GetRequiredService<IPilotFitting>();
        var initial = fixture.Health.Read(pilotId, fixture.Clock.Now).Defense;
        var baseFit = fitting.ShipAttributes(pilotId);
        var initialShot = Fire();

        long start = fixture.Clock.Now;
        long endsAt = start + 3 * TimeSpan.TicksPerSecond;
        long taleId = IncursionRules.TaleId(start);
        IncursionFocus focus = new(taleId, systemId, 20000020, start, endsAt, 1, [new(systemId, IncursionScene.Headquarters)]);
        live.SetIncursionFocus(IncursionRules.HighSecurityLane, focus, start);
        fixture.Clock.Seconds = 1;

        var affected = fixture.Health.Read(pilotId, fixture.Clock.Now).Defense;
        Assert.Equal(initial.ShieldResonance.Scale(1.5), affected.ShieldResonance);
        Assert.Equal(initial.ArmorResonance.Scale(1.5), affected.ArmorResonance);
        Assert.Equal(initial.StructureResonance, affected.StructureResonance);
        Assert.Equal(
            baseFit.GetValueOrDefault(DogmaAttributeId.ShieldKineticResonance, 1) * 1.5,
            fitting.ShipAttributes(pilotId)[DogmaAttributeId.ShieldKineticResonance],
            10
        );
        var affectedShot = Fire();
        Assert.Equal(initialShot.Weapon.Damage.Scale(0.5), affectedShot.Weapon.Damage);
        Assert.Equal(initialShot.Quality, affectedShot.Quality);
        Assert.Null(effects.Read(systemId + 1, fixture.Clock.Now));

        var diminished = focus with { LastUpdated = fixture.Clock.Now, Influence = 0.5 };
        live.SetIncursionFocus(IncursionRules.HighSecurityLane, diminished, fixture.Clock.Now);
        fixture.Clock.Seconds = 2;
        Assert.Equal(initialShot.Weapon.Damage.Scale(0.75), Fire().Weapon.Damage);
        Assert.NotNull(effects.Read(systemId, endsAt - 1));

        fixture.Clock.Seconds = 3;
        Assert.Equal(endsAt, fixture.Clock.Now);
        Assert.Null(effects.Read(systemId, fixture.Clock.Now));
        var restored = fixture.Health.Read(pilotId, fixture.Clock.Now).Defense;
        Assert.Equal(initial.ShieldResonance.Kinetic, restored.ShieldResonance.Kinetic, 10);
        Assert.Equal(initial.ArmorResonance.Explosive, restored.ArmorResonance.Explosive, 10);
        Assert.Equal(initialShot.Weapon.Damage, Fire().Weapon.Damage);

        CombatShot Fire() => fixture.Weapons.Fire(pilotId, fixture.Module, fixture.Npc.Id, SpacePosition.Zero, fixture.Clock.Now, 0.5);
    }

    [Fact]
    public void StagingBountyUsesSameHalfRateForContactsAndSettlement()
    {
        TestClock clock = new();
        using TestWorld fixture = new(liveWorld: true, clock: clock);
        NpcEncounterTests.Prepare(fixture);
        var database = new WorldDatabase(fixture.Path);
        using (CatalogDbContext source = new(database.CatalogPath, writable: true))
        {
            int bountyAttribute = (int)DogmaAttributeId.EntityKillBounty;
            source.Database.ExecuteSql($"INSERT OR IGNORE INTO DogmaAttribute VALUES ({bountyAttribute}, 'entityKillBounty')");
            source.Database.ExecuteSql(
                $"INSERT INTO TypeAttribute VALUES ({NpcEncounterDefinitions.GuristasRookieType}, {bountyAttribute}, 3000)"
            );
        }
        var pilot = fixture.Store.FindPilot(1000001)!;
        long pilotId = pilot.PilotId;
        int systemId = NpcEncounterDefinitions.PerimeterSystem;
        long now = clock.Now;
        long taleId = IncursionRules.TaleId(now);
        var live = LiveWorld.Find(fixture.Path)!;
        var encounters = fixture.Services.GetRequiredService<INpcEncounters>();
        var baseline = encounters.List(systemId, now)[0];
        Assert.True(baseline.BountyCents > 0);
        fixture.Store.SetPosition(pilot.ShipId, baseline.Position + new SpacePosition(1000, 0, 0), systemId);
        live.SetIncursionFocus(
            IncursionRules.HighSecurityLane,
            new(taleId, systemId, 20000020, now, now + TimeSpan.TicksPerSecond, 0, [new(systemId, IncursionScene.Staging)]),
            now
        );

        var affected = encounters.List(systemId, now).Single(npc => npc.Id == baseline.Id);
        Assert.Equal(baseline.BountyCents / 2, affected.BountyCents);
        using var write = live.Economy.Begin(new(systems: [systemId], pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var target = write.Set<WorldNpc>().Find(baseline.Id)!;
        var hit = NpcDamagePersistence.Apply(
            write,
            catalog,
            target,
            write.Pilots.Find(pilotId)!,
            pilot.ShipId,
            PilotFittingStore.ReadAttributes(catalog, target.TypeId),
            new(100_000, 0, 0, 0),
            now,
            bypassResistances: true
        );
        Assert.True(hit.Destroyed);
        var tick = Assert.Single(write.Filter<SavedActivityRewardTick>(row => row.PilotId == pilotId));
        Assert.Equal(affected.BountyCents, tick.Bounties[systemId].AmountCents);
        write.Commit();
    }
}
