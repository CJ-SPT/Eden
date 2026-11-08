using Eden.Gameplay.Dogma;
using Eden.Gameplay.Thermal;
using Eden.World.Repair;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class MutadaptiveRemoteRepairTests
{
    [Fact]
    public void PaidMutadaptiveHeatPreservesTheActualRodivaRampAcrossNormalAndOverloadedCycles()
    {
        using Fixture f = Prepare(thermal: true);
        f.Train(OverloadRules.ThermodynamicsSkill, 3);
        long module = f.Fit(Repairer);
        Space(f);
        long pilot = f.PilotId;
        long ship = f.ShipId;
        long target = Target(f);
        var item = f.Inventory.List(pilot, ship).Single(i => i.Id == module);
        var normal = f.Fitting.ModuleAttributes(pilot, item);
        double amount = normal[DogmaAttributeId.ArmorRepairAmount];
        double bonus = 1 + normal[DogmaAttributeId.OverloadArmorRepairBonus] / 100;
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var store = f.Db.Services.GetRequiredService<PilotRemoteRepairStore>();
        var heat = f.Db.Services.GetRequiredService<IPilotShipHeat>();
        for (int cycle = 0; cycle < 3; cycle++)
        {
            Charge(f, 2500);
            double before = Health(f).Remaining.Armor;
            var paid =
                cycle == 1
                    ? store.Start(pilot, module, Effect, target, new HashSet<long> { target }, f.Clock.Now, continuation: true)
                    : store.StartOverloaded(pilot, module, Effect, target, new HashSet<long> { target }, f.Clock.Now, cycle > 0);
            double expected = amount * (cycle == 1 ? 1 : bonus) * (1 + cycle * paid.Cycle.Module.RampPerCycle);
            Assert.Equal(cycle, paid.Cycle.RampCycles);
            Assert.Equal(expected, paid.Cycle.Module.Amount, 8);
            var thermal = heat.Read(pilot, ship, f.Clock.Now);
            if (cycle == 1)
            {
                Assert.Empty(thermal.Cycles);
            }
            else
            {
                Assert.Equal(paid.Cycle.CompletesAt, Assert.Single(thermal.Cycles).CompletesAt);
            }
            Finish(f, paid);
            Assert.Equal(before + expected, Health(f).Remaining.Armor, 7);
            Assert.Empty(heat.Read(pilot, ship, f.Clock.Now).Cycles);
            Assert.Equal(amount, f.Fitting.ModuleAttributes(pilot, item)[DogmaAttributeId.ArmorRepairAmount], 8);
        }
    }
}
