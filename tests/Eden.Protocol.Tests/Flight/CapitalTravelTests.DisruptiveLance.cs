using Eden.Data.World.Combat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Fact]
    public async Task BaneDisruptiveLanceRequiresSiegeAndDeniesWarpAndHalfTheRepairUntilExpiry()
    {
        const int bane = 77283;
        const int armorRepairer = 11357;
        using Fixture fixture = new(bane, Helium, Rapier, CapitalTravelRules.StandardGenerator);
        long lance = fixture.FitCapitalModule(DoomsdayFieldRules.Azmaru);
        long siege = fixture.FitCapitalModule(CapitalModeRules.SiegeOne);
        long repair = fixture.FitCapitalModule(armorRepairer);
        long cloak = fixture.FitCapitalModule(11370);
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath))
        {
            foreach (int type in new[] { 77281, 77283, 77284, 77288 })
            {
                Assert.True(HullCatalog.UsesCatalogBonuses(catalog, type));
            }
        }
        fixture.PutInCapitalCargo(Helium, 20_050);
        fixture.PutInCapitalCargo(Strontium, 225);
        var lancer = fixture.Db.Store.FindPilot(1000001)!;
        var victim = fixture.Db.Store.FindPilot(1000002)!;
        var origin = new SpacePosition(1_700_000_000, 0, 0);
        fixture.Db.Store.SetPosition(lancer.ShipId, origin, NullSystem);
        fixture.Db.Store.SetPosition(victim.ShipId, origin + new SpacePosition(9_500, 0, 0), NullSystem);
        using (var action = fixture.Live.Economy.Begin(new(ships: [victim.ShipId])))
        {
            action.Add(
                new ShipHealthState
                {
                    ShipId = victim.ShipId,
                    ArmorDamage = 500,
                    UpdatedAt = fixture.Clock.Now,
                }
            );
            action.Commit();
        }
        using Client source = fixture.Select(1000001);
        using Client target = fixture.Select(1000002);
        var cloaking = Assert.IsType<PilotCallResult.Failure>(source.Call("dogmaIM", "Activate", cloak, "cloakingPrototype", null, 0));
        Assert.Contains("fitted disruptive lance", cloaking.Message);
        var rejected = Assert.IsType<PilotCallResult.Failure>(Aim());
        Assert.Contains("Activate siege", rejected.Message);
        Assert.Equal(20_050, CargoFuel());
        var sieged = source.Call("dogmaIM", "Activate", siege, "moduleBonusSiegeModule", null, 0);
        Assert.True(sieged is PilotCallResult.Success, sieged.ToString());
        var fired = Aim();
        Assert.True(fired is PilotCallResult.Success, fired.ToString());
        Assert.Equal(50, CargoFuel());
        var weapons = fixture.Db.Services.GetRequiredService<IPilotAreaWeapons>();
        var field = Assert.Single(weapons.DoomsdayFields(NullSystem, fixture.Clock.Now));
        Assert.Equal(origin + new SpacePosition(61_700, 0, 0), field.Beacon!.Position);
        fixture.Clock.Seconds += 15;
        await target.TickAsync();
        Assert.Contains(target.Packets, packet => ContainsText(packet, "effects.SuperWeaponLanceAmarr"));
        var warp = fixture.Db.Services.GetRequiredService<IPilotWarpDrive>();
        var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
        Assert.Contains("disruptive lance", Assert.Throws<InvalidDataException>(() => Warp()).Message);
        Assert.Contains(
            "disruptive lance",
            Assert.Throws<InvalidDataException>(() => docking.Dock(victim.PilotId, victim.ShipId, JanStation)).Message
        );

        long ends = fixture.Clock.Now + TimeSpan.FromMinutes(1).Ticks;
        source.Dispose();
        target.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.Contains("disruptive lance", Assert.Throws<InvalidDataException>(() => Warp()).Message);
        var fitting = fixture.Db.Services.GetRequiredService<IPilotFitting>();
        var item = fixture.Inventory.List(lancer.PilotId, lancer.ShipId).Single(row => row.Id == repair);
        var repairer = fitting.ModuleAttributes(lancer.PilotId, item);
        double armorBeforeRepair = fixture.Live.Economy.Inspect(world => world.Set<ShipHealthState>().Find(victim.ShipId)!.ArmorDamage);
        var remote = fixture.Db.Services.GetRequiredService<IPilotRemoteRepairing>();
        var applied = remote.Start(
            lancer.PilotId,
            repair,
            DogmaEffect.RemoteArmorRepair,
            victim.ShipId,
            new HashSet<long> { victim.ShipId },
            fixture.Clock.Now
        );
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(applied.Cycle.CompletesAt - fixture.Clock.Now));
        remote.Complete(lancer.PilotId, repair, fixture.Clock.Now);
        double armorDamage = fixture.Live.Economy.Inspect(world => world.Set<ShipHealthState>().Find(victim.ShipId)!.ArmorDamage);
        Assert.Equal(armorBeforeRepair - repairer[DogmaAttributeId.ArmorRepairAmount] / 2, armorDamage, 6);

        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(ends - fixture.Clock.Now - 1));
        Assert.Contains("disruptive lance", Assert.Throws<InvalidDataException>(() => Warp()).Message);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.True(Warp().Cost > 0);

        int CargoFuel() =>
            fixture
                .Inventory.List(lancer.PilotId, lancer.ShipId)
                .Where(item => item.TypeId == Helium && item.FlagId == (int)InventoryFlag.Cargo)
                .Sum(item => item.Quantity);

        WarpCapacitorPayment Warp() => warp.Activate(victim.PilotId, victim.ShipId, 1_000_000, fixture.Clock.Now);

        PilotCallResult Aim() =>
            source.Call(
                "superWeaponMgr",
                "ActivateSinglePointTargetedModule",
                lance,
                (long)DogmaEffect.DisruptiveLance,
                new object?[] { origin.X + 60_000, origin.Y, origin.Z }
            );
    }
}
