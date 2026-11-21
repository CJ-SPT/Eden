using Eden.Data.World.Drones;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Fighters;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Drones;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Fact]
    public async Task EquiteCanLockAndDamageAHostileFighterSquadronWithoutDamagingItsCarrier()
    {
        const int equite = 40358;
        using Fixture fixture = new(Archon, Helium);
        var source = fixture.Db.Store.FindPilot(1000001)!;
        var enemy = fixture.Db.Store.FindPilot(1000002)!;
        fixture.Db.Store.SetPosition(enemy.ShipId, s_station, Jan);
        var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
        long enemyShip = fixture.AssembleAndBoard(docking.Dock(enemy.PilotId, enemy.ShipId, JanStation), Archon);
        var origin = new SpacePosition(1_000_000_000, 0, 0);
        fixture.Db.Store.SetPosition(source.ShipId, origin, NullSystem);
        fixture.Db.Store.SetPosition(enemyShip, origin + new SpacePosition(12_000, 0, 0), NullSystem);
        long interceptor = fixture.PutInFighterBay(equite, 12);
        var attributes = fixture.Db.Services.GetRequiredService<IPilotFitting>().Attributes(Templar);
        fixture.Train(
            enemy.PilotId,
            InventoryDefinitions
                .RequiredSkills.Select(req => ((int)attributes.GetValueOrDefault(req.Skill), (int)attributes.GetValueOrDefault(req.Level)))
                .Where(req => req.Item1 > 0)
                .ToArray()
        );
        long victim = fixture.Grant(enemy.PilotId, enemyShip, InventoryFlag.FighterBay, Templar, 6);
        int tube = FighterDeckRules.FirstTube;
        using Client attacker = fixture.Select(1000001);
        using Client defender = fixture.Select(1000002);
        Launch(defender, victim);
        Launch(attacker, interceptor);
        await attacker.TickAsync();
        var targeting = attacker.Call("dogmaIM", "AddTarget", victim);
        Assert.True(targeting is PilotCallResult.Success, targeting.ToString());
        Assert.IsType<PilotCallResult.Success>(attacker.Call("dogmaIM", "AddTarget", enemyShip));
        fixture.Clock.Seconds += 180;
        await attacker.TickAsync();

        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath);
        var ownDeck = fixture.Db.Services.GetRequiredService<IPilotFighterDeck>();
        var effects = fixture.Db.Services.GetRequiredService<IPilotElectronicWarfare>();
        double normalSpeed = ReadSquadron().Profile.MaximumSpeed;
        Assert.Contains("hostile drone or fighter", Assert.IsType<PilotCallResult.Failure>(Tackle(enemyShip)).Message);
        var tackled = Tackle(victim);
        Assert.True(tackled is PilotCallResult.Success, tackled.ToString());
        var effect = Assert.Single(effects.Incoming(victim));
        Assert.Equal(DogmaEffect.FighterTackle, effect.Module.Effect);
        Assert.Equal(-4, effect.Applied!.WebPercent);
        Assert.Equal(1, effect.Applied.WarpStrength);
        Assert.Equal(normalSpeed * .96, ReadSquadron().Profile.MaximumSpeed, precision: 3);
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, effect.ExpiresAt - effect.StartedAt);
        Assert.Single(attacker.Context.NotificationsBeforeFlight.OfType<FighterTackleStarted>());

        var health = fixture.Db.Services.GetRequiredService<Eden.Gameplay.Combat.IPilotHealth>();
        var carrierHealth = health.Read(enemy.PilotId, fixture.Clock.Now).Remaining;
        var attack = attacker.Call("fighterMgr", "CmdActivateAbilitySlots", new List<object?> { interceptor }, 0, victim);
        Assert.True(attack is PilotCallResult.Success, attack.ToString());
        Assert.NotNull(Assert.Single(ownDeck.Read(source.PilotId, source.ShipId).Squadrons).Tackle);
        var initial = ReadSquadron();
        double finalShield = initial.Health.Remaining.Shield;
        for (int attempt = 0; attempt < 20 && finalShield >= initial.Health.Remaining.Shield; attempt++)
        {
            fixture.Clock.Seconds++;
            await attacker.TickAsync();
            finalShield = ReadSquadron().Health.Remaining.Shield;
        }
        Assert.True(finalShield < initial.Health.Remaining.Shield);
        Assert.Equal(carrierHealth, health.Read(enemy.PilotId, fixture.Clock.Now).Remaining);
        if (fixture.Clock.Now < effect.ExpiresAt)
        {
            fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(effect.ExpiresAt - fixture.Clock.Now));
        }
        await attacker.TickAsync();
        Assert.Empty(effects.Incoming(victim));
        Assert.Equal(normalSpeed, ReadSquadron().Profile.MaximumSpeed);
        Assert.IsType<PilotCallResult.Success>(Tackle(victim));
        Assert.IsType<PilotCallResult.Success>(attacker.Call("fighterMgr", "RecallFightersToTubes", new List<object?> { interceptor }));
        Assert.Empty(effects.Incoming(victim));

        PilotCallResult Tackle(long target) =>
            attacker.Call("fighterMgr", "CmdActivateAbilitySlots", new List<object?> { interceptor }, 2L, target);

        void Launch(Client pilot, long stack)
        {
            var load = Assert.IsType<PilotCallResult.Success>(pilot.Call("fighterMgr", "LoadFightersToTube", stack, tube));
            Assert.True(Assert.IsType<bool>(load.Reply.ToBlue()));
            var launch = pilot.Call("fighterMgr", "LaunchFightersFromTubes", new List<object?> { tube });
            Assert.True(launch is PilotCallResult.Success, launch.ToString());
        }

        Eden.Gameplay.Drones.DroneContact ReadSquadron() =>
            fixture.Live.Economy.Inspect(world => PilotDroneStore.Contact(world, catalog, world.Set<WorldDrone>().Find(victim)!));
    }
}
