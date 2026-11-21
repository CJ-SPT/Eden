using Eden.Data.World.Fittings;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TitanLanceGivesAnEvadeWindowAndAppliesSignatureScaledPulses(bool evade)
    {
        using Fixture fixture = new(structures: true);
        var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
        var original = fixture.Db.Store.FindPilot(1000001)!;
        var targetPilot = fixture.Db.Store.FindPilot(1000002)!;
        var basePosition = new SpacePosition(1_000_000_000, 0, 0);
        var origin = basePosition + new SpacePosition(500_000, 0, 0);
        long keepstar = fixture.AddUpwell(Keepstar, NullSystem, basePosition, original.CorpId);
        fixture.Db.Store.SetPosition(original.ShipId, basePosition, NullSystem);
        long avatar = fixture.AssembleAndBoard(docking.Dock(original.PilotId, original.ShipId, keepstar), Avatar);
        long module = fixture.FitCapitalModule(DoomsdayFieldRules.HolyDestiny);
        fixture.Grant(original.PilotId, avatar, InventoryFlag.Cargo, Helium, 55_000);
        fixture.Db.Store.SetPosition(avatar, origin, Jan);
        using (Client lowsec = fixture.Select(1000001))
        {
            Assert.IsType<PilotCallResult.Failure>(Aim(lowsec, origin + new SpacePosition(200_000, 0, 0)));
            Assert.Equal(55_000, Quantity(fixture, lowsec, Helium));
        }
        fixture.Db.Store.SetPosition(avatar, origin, NullSystem);
        fixture.Db.Store.SetPosition(targetPilot.ShipId, origin + new SpacePosition(9_500, 1_700, 0), NullSystem);
        using Client titan = fixture.Select(1000001);
        using Client target = fixture.Select(1000002);
        var weapons = fixture.Db.Services.GetRequiredService<IPilotAreaWeapons>();
        var health = fixture.Db.Services.GetRequiredService<IPilotHealth>();
        var fitting = fixture.Db.Services.GetRequiredService<IPilotFitting>();
        var defender = fitting.ShipAttributes(targetPilot.PilotId);
        var initial = health.Read(targetPilot.PilotId, fixture.Clock.Now);
        Assert.IsType<PilotCallResult.Failure>(Aim(titan, origin));
        Assert.Equal(55_000, Quantity(fixture, titan, Helium));
        var aim = origin + new SpacePosition(200_000, 0, 0);
        SetCapacitor(34_999);
        Assert.IsType<PilotCallResult.Failure>(Aim(titan, aim));
        Assert.Equal(55_000, Quantity(fixture, titan, Helium));
        Assert.Empty(weapons.DoomsdayFields(NullSystem, fixture.Clock.Now));
        Assert.Null(fixture.Live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(targetPilot.ShipId)));
        SetCapacitor(fitting.ShipAttributes(original.PilotId)[DogmaAttributeId.CapacitorCapacity]);
        var activated = Aim(titan, aim);
        Assert.True(activated is PilotCallResult.Success, activated.ToString());
        Assert.Equal(5_000, Quantity(fixture, titan, Helium));
        Assert.True(fixture.Travel.DoomsdayMovementLocked(avatar));
        Assert.IsType<PilotCallResult.Failure>(titan.Context.Flight.Execute(new FlightCommand.Heading(0, 1, 0)));
        var beam = Assert.Single(weapons.DoomsdayFields(NullSystem, fixture.Clock.Now));
        var anchor = Assert.IsType<StaticSpaceObject>(beam.Beacon);
        Assert.Equal(new SpacePosition(1_000_706_800, 0, 0), anchor.Position);
        Assert.Equal(DoomsdayFieldRules.BeaconType, anchor.TypeId);
        double neutralized =
            35_000
            * defender[DogmaAttributeId.SignatureRadius]
            / 20_000
            * defender.GetValueOrDefault(DogmaAttributeId.EnergyWarfareResistance, 1);
        double charge = fixture.Live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(targetPilot.ShipId)!.Charge);
        Assert.Equal(defender[DogmaAttributeId.CapacitorCapacity] - neutralized, charge, 6);
        var updates = titan.Context.Flight.Updates.Select(update => update.Command).ToList();
        int beacon = updates.FindIndex(command =>
            command is DestinyCommand.AddBalls add && add.Addition.Slims.OfType<StaticSlimItem>().Any(slim => slim.Body.Id == anchor.Id)
        );
        int effect = updates.FindIndex(command =>
            command is DestinyCommand.TimedSpecialEffect { Graphic: "effects.SuperWeaponLanceAmarr", Start: true } fx
            && fx.TargetId == anchor.Id
        );
        Assert.True(beacon >= 0 && effect > beacon);

        await target.TickAsync();
        Assert.Contains(target.Packets, packet => ContainsText(packet, "effects.SuperWeaponLanceAmarr"));
        if (evade)
        {
            Assert.IsType<PilotCallResult.Success>(target.Context.Flight.Execute(new FlightCommand.Heading(0, 1, 0)));
        }
        for (int second = 1; second < 15; second++)
        {
            fixture.Clock.Seconds += 1;
            await target.TickAsync();
        }
        Assert.Equal(initial.Remaining, health.Read(targetPilot.PilotId, fixture.Clock.Now).Remaining);

        fixture.Clock.Seconds += 1;
        await target.TickAsync();
        var shot = health.Read(targetPilot.PilotId, fixture.Clock.Now);
        if (evade)
        {
            Assert.True(target.Pilot.Position!.Y > 2_600);
            Assert.Equal(initial.Remaining, shot.Remaining);
        }
        else
        {
            var full = 50_000 * 1.10;
            var incoming = new DamageVector(full * defender[DogmaAttributeId.SignatureRadius] / 5_000, 0, 0, 0);
            var expected = CombatDamage.Apply(initial.Remaining, initial.Defense, incoming, targetPilot.ShipId, fixture.Clock.Now);
            Assert.Equal(expected.Remaining.Shield, shot.Remaining.Shield, 6);
            Assert.Equal(expected.Remaining.Armor, shot.Remaining.Armor, 6);
            Assert.Equal(expected.Remaining.Structure, shot.Remaining.Structure, 6);
            weapons.Advance(NullSystem, fixture.Clock.Now);
            Assert.Equal(shot.Remaining, health.Read(targetPilot.PilotId, fixture.Clock.Now).Remaining);
        }

        target.Dispose();
        titan.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.True(fixture.Travel.DoomsdayMovementLocked(avatar));
        Assert.Empty(weapons.DoomsdayFields(NullSystem, fixture.Clock.Now));
        fixture.Clock.Seconds += 15;
        Assert.False(fixture.Travel.DoomsdayMovementLocked(avatar));
        Assert.Contains(
            "doomsday cooldown",
            Assert.Throws<InvalidDataException>(() => docking.Dock(original.PilotId, avatar, keepstar)).Message
        );

        void SetCapacitor(double capacity)
        {
            using var action = fixture.Live.Economy.Begin(new(ships: [avatar]));
            var energy = action.Set<ShipEnergy>().Find(avatar) ?? action.Add(new ShipEnergy { ShipId = avatar }).Entity;
            energy.Charge = capacity;
            energy.UpdatedAt = fixture.Clock.Now;
            action.Commit();
        }

        PilotCallResult Aim(Client client, SpacePosition point) =>
            client.Call(
                "superWeaponMgr",
                "ActivateSinglePointTargetedModule",
                module,
                (long)DogmaEffect.DoomsdayBeam,
                new object?[] { point.X, point.Y, point.Z }
            );
    }
}
