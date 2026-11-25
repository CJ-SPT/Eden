using Eden.Data.World.Combat;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    private const int FlexArmorOne = 41515;
    private const int FlexShieldTwo = 41528;
    private const int ArmorEmScript = 41517;
    private const int ShieldKineticScript = 41523;
    private const int Avalanche = 81040;
    private const int FlexHardenerTestHull = 19726; // Phoenix

    [Fact]
    public void CapitalFlexVariantsAndScriptsHavePinnedOperatingProfiles()
    {
        using Fixture fixture = new(FlexHardenerTestHull);
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath);
        int[] armor = [41515, 41525, 41526, 41527];
        int[] shield = [41516, 41528, 41529, 41530, 41531];
        foreach (int type in armor.Concat(shield))
        {
            bool armorType = armor.Contains(type);
            var definition = FittingCatalog.Definition(catalog, type);
            Assert.NotNull(definition);
            Assert.Equal(armorType ? ModuleRack.Low : ModuleRack.Medium, definition.Rack);
            Assert.Equal(
                armorType ? DogmaEffect.ActiveArmorResistance : DogmaEffect.ActiveShieldResistance,
                FittingCatalog.DefaultEffect(catalog, type)
            );
            PilotFittingStore.CheckHull(catalog, type, FlexHardenerTestHull);
            Assert.Throws<InvalidDataException>(() => PilotFittingStore.CheckHull(catalog, type, 601));
        }
        for (int type = 41517; type <= 41524; type++)
        {
            int group = catalog.RequireItem(type).GroupId;
            Assert.True(AmmunitionCatalog.Supported(catalog, type, group));
            Assert.Equal(4, ScriptCatalog.Modifiers(catalog, type).Count);
        }
        PilotFittingStore.CheckHull(catalog, FlexShieldTwo, Avalanche);
        Assert.Contains(HullCatalog.Capabilities(catalog, Avalanche), capability => capability is { EffectId: 12071, Operational: true });
    }

    [Theory]
    [InlineData(
        FlexArmorOne,
        ArmorEmScript,
        false,
        DogmaAttributeId.ArmorEmResonance,
        DogmaAttributeId.ArmorThermalResonance,
        16,
        2550,
        54000
    )]
    [InlineData(
        FlexShieldTwo,
        ShieldKineticScript,
        true,
        DogmaAttributeId.ShieldKineticResonance,
        DogmaAttributeId.ShieldEmResonance,
        19,
        1440,
        36000
    )]
    public async Task CapitalFlexHardenerUsesNativePaidCyclesAndRacialScript(
        int type,
        int script,
        bool shield,
        DogmaAttributeId focused,
        DogmaAttributeId other,
        double percent,
        double cost,
        double duration
    )
    {
        using Fixture fixture = new(FlexHardenerTestHull);
        long module = fixture.FitCapitalModule(type);
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        long pilotId = pilot.PilotId;
        long ship = pilot.ShipId;
        var fitting = fixture.Db.Services.GetRequiredService<IPilotFitting>();
        var ammunition = fixture.Db.Services.GetRequiredService<IPilotAmmunition>();
        var resources = fixture.Db.Services.GetRequiredService<IPilotShipResources>();
        var before = fitting.ShipAttributes(pilotId);
        string effect = shield ? "modifyActiveShieldResonancePostPercent" : "modifyActiveArmorResonancePostPercent";
        var native = shield ? DogmaEffect.ActiveShieldResistance : DogmaEffect.ActiveArmorResistance;
        long scriptStack = fixture.Grant(pilotId, ship, InventoryFlag.Cargo, script, 2);
        long wrongScript = fixture.Grant(pilotId, ship, InventoryFlag.Cargo, shield ? ArmorEmScript : ShieldKineticScript, 1);
        using Client client = fixture.Select(1000001);
        OpenCycle(percent, percent);
        await EndCycle();

        Assert.IsType<PilotCallResult.Failure>(client.Call("dogmaIM", "LoadAmmo", ship, module, wrongScript, ship));
        Assert.Same(PilotCallResult.None, client.Call("dogmaIM", "LoadAmmo", ship, module, scriptStack, ship));
        Assert.Equal(script, Assert.Single(ammunition.Read(pilotId, ship)).TypeId);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(1));
        OpenCycle(3 * percent, 0);
        Assert.Equal(1, Assert.Single(ammunition.Read(pilotId, ship)).Quantity);
        using (var checkpoint = fixture.Live.CaptureSnapshot())
        {
            Assert.NotNull(checkpoint);
            checkpoint.Project();
            using WorldDbContext saved = new(checkpoint.Database);
            Assert.Equal(
                fixture.Clock.Now + (long)duration * TimeSpan.TicksPerMillisecond,
                saved.Set<ModuleCooldown>().Find(module)!.ReadyAt
            );
        }
        await EndCycle();

        void OpenCycle(double focusedBonus, double otherBonus)
        {
            double charge = resources.Capacitor(pilotId, fixture.Clock.Now);
            Assert.IsType<PilotCallResult.Success>(client.Call("dogmaIM", "Activate", module, effect, null, 0L));
            Assert.Equal(charge - cost, resources.Capacitor(pilotId, fixture.Clock.Now), 7);
            var active = fitting.ShipAttributes(pilotId);
            Assert.Equal(before[focused] * (1 - focusedBonus / 100), active[focused], 7);
            Assert.Equal(before[other] * (1 - otherBonus / 100), active[other], 7);
            var start = client.Context.Notifications.OfType<ShipEffectChanged>().Last(n => n.Active && n.Effect.ModuleId == module);
            Assert.Equal(native, start.Effect.Effect);
            Assert.Equal(duration, start.Effect.DurationMilliseconds);
        }

        async Task EndCycle()
        {
            Assert.IsType<PilotCallResult.Success>(client.Call("dogmaIM", "Deactivate", module, effect));
            Assert.NotEqual(before[focused], fitting.ShipAttributes(pilotId)[focused]);
            fixture.Clock.Milliseconds += (long)duration;
            await client.TickAsync();
            Assert.Equal(before[focused], fitting.ShipAttributes(pilotId)[focused], 7);
        }
    }
}
