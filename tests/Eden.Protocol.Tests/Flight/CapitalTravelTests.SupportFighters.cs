using Eden.Data.World.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Drones;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Theory]
    [InlineData(40346, DogmaEffect.WarpDisruptor, 3, 0)]
    [InlineData(40347, DogmaEffect.StasisWebifier, 0, -30)]
    [InlineData(37599, DogmaEffect.NpcEnergyNeutralization, 0, 0)]
    [InlineData(40345, DogmaEffect.PlayerEcm, 0, 0)]
    public async Task CarrierSupportFightersApplyTheirSlotZeroAbilitiesAndClearThemOnRecall(
        int type,
        DogmaEffect effect,
        double strength,
        double web
    )
    {
        using Fixture fixture = new(Archon, Helium);
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath))
        {
            foreach (int support in new[] { 37599, 40345, 40346, 40347, 40568, 40569, 40570, 40571 })
            {
                Assert.NotNull(DroneCatalog.Profile(catalog, support).Utility);
            }
        }
        var source = fixture.Db.Store.FindPilot(1000001)!;
        var victim = fixture.Db.Store.FindPilot(1000002)!;
        var point = new SpacePosition(1_000_000_000, 0, 0);
        fixture.Db.Store.SetPosition(source.ShipId, point, NullSystem);
        fixture.Db.Store.SetPosition(victim.ShipId, point + new SpacePosition(6500, 0, 0), NullSystem);
        long first = fixture.PutInFighterBay(type, 3);
        long reserve = fixture.PutInFighterBay(type, 6);
        int tube = FighterDeckRules.FirstTube;
        using Client carrier = fixture.Select(1000001);
        using Client target = fixture.Select(1000002);
        var deck = fixture.Db.Services.GetRequiredService<IPilotFighterDeck>();
        Assert.True(Boolean(carrier.Call("fighterMgr", "LoadFightersToTube", first, tube)));
        Assert.True(Boolean(carrier.Call("fighterMgr", "LoadFightersToTube", reserve, tube + 1)));
        Assert.False(Boolean(carrier.Call("fighterMgr", "LoadFightersToTube", reserve, tube + 2)));
        var launch = carrier.Call("fighterMgr", "LaunchFightersFromTubes", new List<object?> { tube });
        Assert.True(launch is PilotCallResult.Success, launch.ToString());
        Assert.Equal(3, Assert.Single(deck.Read(source.PilotId, source.ShipId).Squadrons).Count);
        Assert.IsType<PilotCallResult.Success>(carrier.Call("dogmaIM", "AddTarget", victim.ShipId));
        fixture.Clock.Seconds += 180;
        await carrier.TickAsync();
        var resources = fixture.Db.Services.GetRequiredService<IPilotShipResources>();
        double capacitor = resources.Capacitor(victim.PilotId, fixture.Clock.Now);
        var attack = carrier.Call("fighterMgr", "CmdActivateAbilitySlots", new List<object?> { first }, 0, victim.ShipId);
        Assert.True(attack is PilotCallResult.Success, attack.ToString());
        var tackle = fixture.Db.Services.GetRequiredService<IPilotTackle>();
        fixture.Clock.Seconds++;
        await carrier.TickAsync();
        await target.TickAsync();
        switch (effect)
        {
            case DogmaEffect.NpcEnergyNeutralization:
                Assert.True(resources.Capacitor(victim.PilotId, fixture.Clock.Now) < capacitor);
                break;
            case DogmaEffect.PlayerEcm:
                Assert.True(HasJammerCycle());
                break;
            default:
                var pulse = Assert.Single(tackle.Incoming(victim.ShipId));
                Assert.Equal(effect, pulse.Module.Effect);
                Assert.Equal(strength, pulse.Module.Strength);
                Assert.Equal(web, pulse.Module.WebPercent);
                Assert.Equal(0, pulse.Module.MwdBlock);
                break;
        }

        Assert.IsType<PilotCallResult.Success>(carrier.Call("fighterMgr", "RecallFightersToTubes", new List<object?> { first }));
        fixture.Clock.Seconds++;
        await target.TickAsync();
        Assert.Empty(tackle.Incoming(victim.ShipId));
        Assert.False(HasJammerCycle());

        bool HasJammerCycle() =>
            fixture.Live.Economy.Inspect(world =>
                world.Set<WorldPlayerEwar>().Any(row => row.ModuleId == first && row.TargetId == victim.ShipId)
            );

        static bool Boolean(PilotCallResult result) => Assert.IsType<bool>(Assert.IsType<PilotCallResult.Success>(result).Reply.ToBlue());
    }
}
