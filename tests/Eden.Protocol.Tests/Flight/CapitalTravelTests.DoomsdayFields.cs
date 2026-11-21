using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Theory]
    [InlineData(DoomsdayFieldRules.DivineHarvest, true)]
    [InlineData(DoomsdayFieldRules.BosonicFieldGenerator, true)]
    [InlineData(DoomsdayFieldRules.BosonicFieldGenerator, false)]
    public async Task AreaDoomsdaysUseNativeAimAndSettleAtTheCurrentSweepOrCone(int weapon, bool intersects)
    {
        using Fixture fixture = new(structures: true);
        bool sweep = weapon == DoomsdayFieldRules.DivineHarvest;
        int fuel = sweep ? Helium : Strontium;
        int quantity = sweep ? 50_000 : 2_000;
        var origin = new SpacePosition(1_500_000_000, 0, 0);
        var station = origin - new SpacePosition(500_000, 0, 0);
        var source = fixture.Db.Store.FindPilot(1000001)!;
        var victim = fixture.Db.Store.FindPilot(1000002)!;
        long keepstar = fixture.AddUpwell(Keepstar, NullSystem, station, source.CorpId);
        fixture.Db.Store.SetPosition(source.ShipId, station, NullSystem);
        var docked = fixture.Db.Services.GetRequiredService<IStationDocking>().Dock(source.PilotId, source.ShipId, keepstar);
        long ship = fixture.AssembleAndBoard(docked, Avatar);
        long module = fixture.FitCapitalModule(weapon);
        fixture.Grant(source.PilotId, ship, InventoryFlag.Cargo, fuel, quantity + 10);
        fixture.Db.Store.SetPosition(ship, origin, NullSystem);
        var offset = sweep ? new SpacePosition(75_000, 0, 0) : new SpacePosition(20_000, intersects ? 11_000 : 15_000, 0);
        fixture.Db.Store.SetPosition(victim.ShipId, origin + offset, NullSystem);
        using Client titan = fixture.Select(1000001);
        using Client observer = fixture.Select(1000002);
        var health = fixture.Db.Services.GetRequiredService<IPilotHealth>();
        var initial = health.Read(victim.PilotId, fixture.Clock.Now).Remaining;
        var weapons = fixture.Db.Services.GetRequiredService<IPilotAreaWeapons>();
        double halfSpan = Math.Tan(Math.PI / 12) * 150_000;
        var first = origin + new SpacePosition(156_800, -halfSpan, 0);
        var last = origin + new SpacePosition(156_800, halfSpan, 0);
        PilotCallResult Start() =>
            sweep
                ? titan.Call("superWeaponMgr", "ActivateSlashModule", module, Point(first), Point(last))
                : titan.Call(
                    "superWeaponMgr",
                    "ActivateSinglePointTargetedModule",
                    module,
                    (long)DogmaEffect.DoomsdayCone,
                    Point(origin + new SpacePosition(36_800, 0, 0))
                );

        if (sweep)
        {
            Assert.IsType<PilotCallResult.Failure>(titan.Call("superWeaponMgr", "ActivateSlashModule", module, Point(first), Point(first)));
            Assert.Equal(quantity + 10, Quantity(fixture, titan, fuel));
        }
        var activated = Start();
        Assert.True(activated is PilotCallResult.Success, activated.ToString());
        Assert.Equal(10, Quantity(fixture, titan, fuel));
        var field = Assert.Single(weapons.DoomsdayFields(NullSystem, fixture.Clock.Now));
        Assert.Null(field.Beacon);
        await observer.TickAsync();
        if (!sweep)
        {
            Assert.Contains(observer.Packets, packet => ContainsText(packet, "effects.IndicatorBeamMoving"));
        }
        fixture.Clock.Seconds += 9;
        await observer.TickAsync();
        Assert.Equal(initial, health.Read(victim.PilotId, fixture.Clock.Now).Remaining);

        fixture.Clock.Seconds += 1;
        await observer.TickAsync();
        if (sweep)
        {
            Assert.Contains(observer.Packets, packet => ContainsText(packet, "effects.SWSlashBeamAmarr"));
            Assert.Contains(observer.Packets, packet => ContainsText(packet, "startTargetOffset"));
            Assert.Equal(initial, health.Read(victim.PilotId, fixture.Clock.Now).Remaining);
            fixture.Clock.Seconds += 5;
            await observer.TickAsync();
            Assert.True(health.WasDestroyed(victim.ShipId));
        }
        else
        {
            Assert.Equal(!intersects, initial == health.Read(victim.PilotId, fixture.Clock.Now).Remaining);
            Assert.False(health.WasDestroyed(victim.ShipId));
        }

        static object?[] Point(SpacePosition position) => [position.X, position.Y, position.Z];
    }
}
