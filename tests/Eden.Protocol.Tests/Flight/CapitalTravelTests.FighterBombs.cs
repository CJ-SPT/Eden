using Eden.Data.World.Combat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;
using Eden.World.Combat;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Theory]
    [InlineData(Jan, false)]
    [InlineData(NullSystem, false)]
    [InlineData(NullSystem, true)]
    public async Task AmetatBombResolvesIndependentlyOfTheSquadronAfterReopen(int system, bool intercept)
    {
        using Fixture fixture = new(Archon, Helium, structures: true);
        var source = fixture.Db.Store.FindPilot(1000001)!;
        var target = fixture.Db.Store.FindPilot(1000002)!;
        var anchor = new SpacePosition(1_000_000_000, 0, 0);
        long keepstar = fixture.AddUpwell(Keepstar, NullSystem, anchor, source.CorpId);
        fixture.Db.Store.SetPosition(source.ShipId, anchor, NullSystem);
        var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
        long ship = fixture.AssembleAndBoard(docking.Dock(source.PilotId, source.ShipId, keepstar), Aeon);
        fixture.Db.Store.SetPosition(ship, anchor + new SpacePosition(500_000, 0, 0), system);
        long fighter = fixture.PutInFighterBay(40362, 3);
        using Client carrier = fixture.Select(1000001);
        int tube = FighterDeckRules.FirstTube;
        Assert.IsType<PilotCallResult.Success>(carrier.Call("fighterMgr", "LoadFightersToTube", fighter, tube));
        Assert.IsType<PilotCallResult.Success>(carrier.Call("fighterMgr", "LaunchFightersFromTubes", new List<object?> { tube }));
        var drone = Assert.Single(
            fixture.Db.Services.GetRequiredService<IPilotDrones>().List(system, fixture.Clock.Now),
            drone => drone.Id == fighter
        );
        var aim = drone.Position + new SpacePosition(0, 30_000, 0);
        fixture.Db.Store.SetPosition(target.ShipId, aim, system);
        var areas = fixture.Db.Services.GetRequiredService<IPilotAreaWeapons>();
        var deck = fixture.Db.Services.GetRequiredService<IPilotFighterDeck>();
        var health = fixture.Db.Services.GetRequiredService<IPilotHealth>();
        double shield = health.Read(target.PilotId, fixture.Clock.Now).Remaining.Shield;
        var launched = carrier.Call(
            "fighterMgr",
            "CmdActivateAbilitySlots",
            new List<object?> { fighter },
            2L,
            new object?[] { aim.X, aim.Y, aim.Z }
        );
        if (system == Jan)
        {
            Assert.IsType<PilotCallResult.Failure>(launched);
            Assert.Empty(areas.Projectiles(system, fixture.Clock.Now));
            Assert.Equal(3, Assert.Single(deck.Read(source.PilotId, ship).Squadrons).Salvo!.Charges);
            return;
        }
        Assert.True(launched is PilotCallResult.Success, launched.ToString());
        var bomb = Assert.Single(areas.Projectiles(system, fixture.Clock.Now));
        Assert.Equal(41549, bomb.TypeId);
        Assert.Equal(2, Assert.Single(deck.Read(source.PilotId, ship).Squadrons).Salvo!.Charges);
        var flight = fixture.Live.Economy.Inspect(world => world.Set<SavedAreaWeapon>().Find(bomb.Id)!.Flight());
        Assert.Equal(drone.Position, flight.Origin);
        Assert.Equal(1920, flight.Bomb!.Damage.Em);
        Assert.Equal(TimeSpan.FromSeconds(12).Ticks, flight.ResolvesAt - flight.StartedAt);
        Assert.IsType<PilotCallResult.Success>(carrier.Call("fighterMgr", "RecallFightersToTubes", new List<object?> { fighter }));
        carrier.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.Equal(bomb.Id, Assert.Single(areas.Projectiles(system, fixture.Clock.Now)).Id);
        if (intercept)
        {
            using var world = fixture.Live.Economy.Begin(new(systems: [system]));
            Assert.True(
                AreaWeaponStore.DamageProjectiles(world, system, new DamageVector(0, 100000, 0, 0), fixture.Clock.Now, (_, _) => true)
            );
            world.Commit();
        }
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(flight.ResolvesAt - fixture.Clock.Now - 1));
        areas.Advance(system, fixture.Clock.Now);
        Assert.Equal(shield, health.Read(target.PilotId, fixture.Clock.Now).Remaining.Shield);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        areas.Advance(system, fixture.Clock.Now);
        double after = health.Read(target.PilotId, fixture.Clock.Now).Remaining.Shield;
        Assert.Equal(!intercept, after < shield);
        Assert.Empty(areas.Projectiles(system, fixture.Clock.Now));
        areas.Advance(system, fixture.Clock.Now);
        Assert.Equal(after, health.Read(target.PilotId, fixture.Clock.Now).Remaining.Shield);
    }
}
