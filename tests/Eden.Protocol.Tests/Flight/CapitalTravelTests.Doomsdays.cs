using Eden.Data.World.Combat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AvatarJudgmentPaysOnceAndSettlesAgainstALockedRevelation(bool breakLock)
    {
        const int judgment = 24550;
        using Fixture fixture = new(structures: true);
        var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
        var original = fixture.Db.Store.FindPilot(1000001)!;
        var opponent = fixture.Db.Store.FindPilot(1000002)!;
        var point = new SpacePosition(100_000_000, 0, 0);
        long keepstar = fixture.AddUpwell(Keepstar, NullSystem, point, original.CorpId);
        long otherKeepstar = fixture.AddUpwell(Keepstar, NullSystem, point + new SpacePosition(-1_000_000, 0, 0), opponent.CorpId);
        fixture.Db.Store.SetPosition(original.ShipId, point, NullSystem);
        fixture.Db.Store.SetPosition(opponent.ShipId, point + new SpacePosition(-1_000_000, 0, 0), NullSystem);
        long avatar = fixture.AssembleAndBoard(docking.Dock(original.PilotId, original.ShipId, keepstar), Avatar);
        long revelation = fixture.AssembleAndBoard(docking.Dock(opponent.PilotId, opponent.ShipId, otherKeepstar), Revelation);
        long module = fixture.FitCapitalModule(judgment);
        fixture.Train(original.PilotId, (DoomsdayRules.OperationSkill, 2), (DoomsdayRules.RapidFiringSkill, 4));
        fixture.Grant(original.PilotId, avatar, InventoryFlag.Cargo, Helium, 55_000);
        var center = point + new SpacePosition(168_000, 0, 0);
        fixture.Db.Store.SetPosition(avatar, center, NullSystem);
        fixture.Db.Store.SetPosition(revelation, center + new SpacePosition(55_000, 0, 0), NullSystem);
        using Client titan = fixture.Select(1000001);
        using Client target = fixture.Select(1000002);
        var weapons = fixture.Db.Services.GetRequiredService<IPilotAreaWeapons>();

        Assert.IsType<PilotCallResult.Failure>(Fire());
        Assert.Equal(55_000, Quantity(fixture, titan, Helium));
        Assert.IsType<PilotCallResult.Success>(titan.Call("dogmaIM", "AddTarget", revelation));
        fixture.Clock.Seconds += 15;
        await titan.TickAsync();

        var activated = Fire();
        Assert.True(activated is PilotCallResult.Success, activated.ToString());
        Assert.Equal(5_000, Quantity(fixture, titan, Helium));
        Assert.True(fixture.Travel.DoomsdayMovementLocked(avatar));
        Assert.IsType<PilotCallResult.Failure>(titan.Call("dogmaIM", "Deactivate", module, "superWeaponAmarr"));
        await titan.TickAsync();
        await target.TickAsync();
        Assert.Contains(target.Packets, packet => ContainsText(packet, "effects.SuperWeaponAmarr"));

        fixture.Clock.Seconds += 8;
        weapons.Advance(NullSystem, fixture.Clock.Now);
        Assert.Null(Loss());
        if (breakLock)
        {
            Assert.IsType<PilotCallResult.Success>(titan.Call("dogmaIM", "RemoveTarget", revelation));
        }
        fixture.Clock.Seconds += 1;
        weapons.Advance(NullSystem, fixture.Clock.Now);
        if (breakLock)
        {
            Assert.Null(Loss());
            Assert.Equal(revelation, fixture.Db.Store.FindPilot(1000002)!.ShipId);
        }
        else
        {
            Assert.Equal(Revelation, Loss()!.TypeId);
            Assert.NotEqual(revelation, fixture.Db.Store.FindPilot(1000002)!.ShipId);
        }

        var cooldown = fixture.Live.Economy.Inspect(world => world.Set<ModuleCooldown>().Find(module)!);
        Assert.Equal(fixture.Clock.Now + (252 - 9) * TimeSpan.TicksPerSecond, cooldown.ReadyAt);
        fixture.Clock.Seconds += 21;
        Assert.False(fixture.Travel.DoomsdayMovementLocked(avatar));
        Assert.Contains(
            "doomsday cooldown",
            Assert.Throws<InvalidDataException>(() => docking.Dock(original.PilotId, avatar, keepstar)).Message
        );

        titan.Dispose();
        target.Dispose();
        fixture.Db.Store.SetPosition(avatar, point + new SpacePosition(158_000, 0, 0), NullSystem);
        await fixture.Live.FlushAsync();
        fixture.Reopen();
        Assert.Equal(breakLock, Loss() is null);
        fixture.Clock.Seconds += 269;
        Assert.Contains(
            "doomsday cooldown",
            Assert.Throws<InvalidDataException>(() => docking.Dock(original.PilotId, avatar, keepstar)).Message
        );
        fixture.Clock.Seconds += 1;
        Assert.Equal(keepstar, docking.Dock(original.PilotId, avatar, keepstar).DockedLocationId);

        PilotCallResult Fire() => titan.Call("dogmaIM", "Activate", module, "superWeaponAmarr", revelation, 0);
        ShipLoss? Loss() => fixture.Live.Economy.Inspect(world => world.Set<ShipLoss>().Find(revelation));
    }
}
