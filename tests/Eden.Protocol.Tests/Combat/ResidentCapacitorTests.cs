using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class CapacitorWarfareTests
{
    [Fact]
    public void ResidentResourceObserversReuseTheirInputsAndKeepExactSubsecondRechargeWithoutSql()
    {
        using var f = Prepare();
        long module = f.Fit(533);
        Space(f);
        long ship = f.ShipId;
        long target = Target(f);
        using ResidentFitCache fittingCache = new(new(f.Db.Path));
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        Store(f).Activate(f.PilotId, module, DogmaEffect.EnergyNeutralizer, target, new HashSet<long> { target }, f.Clock.Now);
        var resources = f.Db.Services.GetRequiredService<IPilotShipResources>();
        var before = resources.Read(f.PilotId, ship, f.Clock.Now);
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            for (int frame = 0; frame < 32; frame++)
            {
                var current = resources.Read(f.PilotId, ship, f.Clock.Now);
                Assert.Equal(before.Health, current.Health);
                Assert.Equal(before.Capacitor, current.Capacitor);
                Assert.Equal(before.Capacitor, resources.Capacitor(f.PilotId, f.Clock.Now));
            }
        });
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        double expected = ChargedResource.Recharged(
            before.Capacitor,
            before.Attributes[DogmaAttributeId.CapacitorCapacity],
            before.Attributes[DogmaAttributeId.CapacitorRechargeTime],
            1d / TimeSpan.TicksPerMillisecond
        );
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            Assert.Equal(expected, resources.Capacitor(f.PilotId, f.Clock.Now));
            Assert.Equal(expected, resources.Read(f.PilotId, ship, f.Clock.Now).Capacitor);
        });
    }

    [Theory]
    [InlineData(false, 533, DogmaEffect.EnergyNeutralizer, true)]
    [InlineData(true, 533, DogmaEffect.EnergyNeutralizer, true)]
    [InlineData(false, 530, DogmaEffect.EnergyNosferatu, true)]
    [InlineData(true, 530, DogmaEffect.EnergyNosferatu, false)]
    [InlineData(false, 529, DogmaEffect.RemoteCapacitorTransfer, true)]
    public void ResidentCapacitorPaymentRejectsRepeatsAndCheckpointsBothPoolsWithoutSql(
        bool npc,
        int type,
        DogmaEffect effect,
        bool applies
    )
    {
        using var f = Prepare();
        long module = f.Fit(type);
        if (npc)
        {
            NpcSpace(f, source: 10000);
        }
        else
        {
            Space(f);
        }
        long ship = f.ShipId;
        long target = npc ? EnergyNpcId : Target(f);
        using ResidentFitCache fittingCache = new(new(f.Db.Path));
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        CapacitorActivation Fire() => Store(f).Activate(f.PilotId, module, effect, target, new HashSet<long> { target }, f.Clock.Now);
        var first = Fire();
        live.Economy.Warm();
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(first.Cycle.ExpiresAt - f.Clock.Now));
        live.Economy.Read(
            world =>
            {
                using CatalogDbContext catalog = new(f.CatalogPath);
                var actor = world.Pilots.Single(row => row.Id == f.PilotId);
                _ = PilotFittingStore.ReadModuleAttributes(
                    world,
                    catalog,
                    f.PilotId,
                    PilotInventoryStore.Read(catalog, world.Set<OwnedItem>().Find(module)!)
                );
                _ = PilotFittingStore.ShipAttributes(world, catalog, actor);
                if (!npc)
                {
                    _ = PilotFittingStore.ShipAttributes(world, catalog, world.Pilots.Single(row => row.ActiveShipId == target));
                }
                return true;
            },
            new(pilots: [f.PilotId], ships: npc ? [ship] : [ship, target])
        );
        CapacitorActivation? result = null;
        List<string> commands = [];
        using (
            var metrics = SessionPerformance.Begin(
                "resident capacitor payment",
                _ => { },
                force: true,
                command: sql => commands.Add(sql + Environment.NewLine + Environment.StackTrace)
            )!
        )
        {
            result = Fire();
            Assert.Throws<InvalidDataException>(() => Fire());
            Store(f).Stop(f.PilotId, ship, module);
            Assert.True(metrics.Commands == 0, string.Join(Environment.NewLine, commands));
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        var paid = Assert.IsType<CapacitorActivation>(result);
        if (applies)
        {
            Assert.True(paid.Applied > 0);
        }
        else
        {
            // Native NOS compares absolute GJ; this source has more charge than the NPC.
            Assert.Equal(0, paid.Applied);
        }
        Assert.Equal(paid.Capacitor, live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(ship)!.Charge), 8);
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        Assert.Equal(paid.Capacitor, reopened.Set<ShipEnergy>().Find(ship)!.Charge, 8);
        double recipient = npc ? reopened.Set<WorldNpcEnergy>().Find(target)!.Charge : reopened.Set<ShipEnergy>().Find(target)!.Charge;
        Assert.Equal(paid.TargetCapacitor, recipient, 8);
        Assert.Equal(paid.Cycle.ExpiresAt, reopened.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Null(reopened.Set<ActiveFittedModule>().Find(module));
    }
}
