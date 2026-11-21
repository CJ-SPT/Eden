using Eden.Data.World.Drones;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Drones;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    private const int Archon = 23757;
    private const int Templar = 23055;
    private const int Cyclops = 32325;

    [Fact]
    public async Task ArchonFighterDeckPreservesShortSquadronsAndLimitsAcrossReopen()
    {
        using Fixture fixture = new(Archon, 16274);
        const int firstTube = FighterDeckRules.FirstTube;
        long first = fixture.PutInFighterBay(Templar, 2);
        long reserve = fixture.PutInFighterBay(Templar, 20);
        long heavy = fixture.PutInFighterBay(Cyclops, 1);
        var deck = fixture.Db.Services.GetRequiredService<IPilotFighterDeck>();
        using Client carrier = fixture.Select(1000001);
        long pilot = carrier.Pilot.PilotId;
        long ship = carrier.Pilot.ShipId;

        Assert.False(Boolean(carrier.Call("fighterMgr", "LoadFightersToTube", heavy, firstTube)));
        Assert.False(Boolean(carrier.Call("fighterMgr", "LoadFightersToTube", reserve, firstTube + 4)));
        Assert.True(Boolean(carrier.Call("fighterMgr", "LoadFightersToTube", first, firstTube)));
        Assert.Equal(new(firstTube, first, Templar, 2), Assert.Single(deck.Read(pilot, ship).Tubes));
        Assert.True(Boolean(carrier.Call("fighterMgr", "LoadFightersToTube", reserve, firstTube)));
        Assert.Equal(new(firstTube, first, Templar, 6), Assert.Single(deck.Read(pilot, ship).Tubes));
        Assert.True(Boolean(carrier.Call("fighterMgr", "LoadFightersToTube", reserve, firstTube + 1)));
        Assert.True(Boolean(carrier.Call("fighterMgr", "LoadFightersToTube", reserve, firstTube + 2)));
        Assert.False(Boolean(carrier.Call("fighterMgr", "LoadFightersToTube", reserve, firstTube + 3)));
        Assert.Equal(20 - 4 - 6 - 6, fixture.Inventory.List(pilot, ship).Single(item => item.Id == reserve).Quantity);
        await carrier.TickAsync();
        Assert.Contains(carrier.Packets, packet => ContainsText(packet, "OnFighterTubeContentUpdate"));
        Assert.Contains(carrier.Packets, packet => ContainsText(packet, "OnFighterTubeTaskStatus"));

        var expected = deck.Read(pilot, ship).Tubes.ToArray();
        carrier.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen();
        Assert.Equal(expected, deck.Read(pilot, ship).Tubes);
        using Client reopened = fixture.Select(1000001);
        var reply = Assert.IsType<object?[]>(
            Assert.IsType<PilotCallResult.Success>(reopened.Call("fighterMgr", "GetFightersForShip")).Reply.ToBlue()
        );
        Assert.Equal(3, Assert.IsType<List<object?>>(reply[0]).Count);
        Assert.Empty(Assert.IsType<List<object?>>(reply[1]));
        Assert.True(Boolean(reopened.Call("fighterMgr", "UnloadTubeToFighterBay", firstTube)));
        Assert.DoesNotContain(deck.Read(pilot, ship).Tubes, tube => tube.Flag == firstTube);
        Assert.Equal((int)InventoryFlag.FighterBay, fixture.Inventory.List(pilot, ship).Single(i => i.Id == first).FlagId);

        static bool Boolean(PilotCallResult result) => Assert.IsType<bool>(Assert.IsType<PilotCallResult.Success>(result).Reply.ToBlue());
    }

    [Fact]
    public async Task ArchonLaunchesAttacksAndRecallsTheSameSquadron()
    {
        using Fixture fixture = new(Archon, 16274, structures: true);
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        var observer = fixture.Db.Store.FindPilot(1000002)!;
        var origin = new SpacePosition(1_000_000_000, 0, 0);
        fixture.Db.Store.SetPosition(pilot.ShipId, origin, NullSystem);
        fixture.Db.Store.SetPosition(observer.ShipId, origin + new SpacePosition(-4000, 0, 0), NullSystem);
        long item = fixture.PutInFighterBay(Templar, 6);
        int tube = FighterDeckRules.FirstTube;
        var deck = fixture.Db.Services.GetRequiredService<IPilotFighterDeck>();
        long target = StructureRules.FirstStructureId + 121L;
        using (var action = fixture.Live.Economy.Begin(new(systems: [NullSystem])))
        {
            action.Add(
                new SavedStructure
                {
                    Id = target,
                    TypeId = 35832,
                    Name = "Astrahus",
                    CorporationId = observer.CorpId,
                    SystemId = NullSystem,
                    X = origin.X + 8500,
                    Y = 0,
                    Z = 0,
                    State = StructureState.ShieldVulnerable,
                    HasCore = true,
                    LowPowerAt = fixture.Clock.Now,
                }
            );
            action.Commit();
        }
        using Client carrier = fixture.Select(1000001);
        using Client witness = fixture.Select(1000002);
        Assert.IsType<PilotCallResult.Success>(carrier.Call("fighterMgr", "LoadFightersToTube", item, tube));
        Assert.IsType<PilotCallResult.Failure>(carrier.Call("fighterMgr", "LaunchFightersFromTubes", new List<object?> { tube, tube + 1 }));
        Assert.Single(deck.Read(pilot.PilotId, pilot.ShipId).Tubes);
        var launch = carrier.Call("fighterMgr", "LaunchFightersFromTubes", new List<object?> { tube });
        Assert.True(launch is PilotCallResult.Success, launch.ToString());
        Assert.Empty(deck.Read(pilot.PilotId, pilot.ShipId).Tubes);
        Assert.Equal(item, Assert.Single(deck.Read(pilot.PilotId, pilot.ShipId).Squadrons).SquadronId);
        await carrier.TickAsync();
        await witness.TickAsync();
        Assert.Contains(carrier.Packets, packet => ContainsText(packet, "OnFighterAddedToController"));
        Assert.Contains(witness.Packets, packet => ContainsText(packet, "squadronSize"));

        Assert.IsType<PilotCallResult.Success>(carrier.Call("dogmaIM", "AddTarget", target));
        fixture.Clock.Seconds += 10;
        await carrier.TickAsync();
        var attack = carrier.Call("fighterMgr", "CmdActivateAbilitySlots", new List<object?> { item }, 0, target);
        Assert.True(attack is PilotCallResult.Success, attack.ToString());
        fixture.Clock.Seconds += 1;
        await carrier.TickAsync();
        Assert.NotNull(fixture.Structures.Read(pilot.PilotId, target)!.Battle!.RepairAt);
        Assert.Contains(carrier.Packets, packet => ContainsText(packet, "OnFighterAbilitySlotActivated"));
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath))
        using (var action = fixture.Live.Economy.Begin(new(pilots: [pilot.PilotId], ships: [pilot.ShipId], systems: [NullSystem])))
        {
            var drone = action.Set<WorldDrone>().Find(item)!;
            var contact = PilotDroneStore.Contact(action, catalog, drone);
            var defense = contact.Profile.Defense;
            double em =
                defense.Shield / defense.ShieldResonance.Em
                + defense.Armor / defense.ArmorResonance.Em
                + defense.Structure / defense.StructureResonance.Em;
            SmartbombStore.DamageDrone(action, drone, contact, new DamageVector(em + 1, 0, 0, 0), fixture.Clock.Now);
            action.Commit();
        }
        fixture.Clock.Seconds += 1;
        await carrier.TickAsync();
        Assert.Equal(5, Assert.Single(deck.Read(pilot.PilotId, pilot.ShipId).Squadrons).Count);
        Assert.Contains(carrier.Packets, packet => ContainsText(packet, "OnInSpaceSquadronSizeChanged"));
        carrier.Dispose();
        witness.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.Equal(5, Assert.Single(deck.Read(pilot.PilotId, pilot.ShipId).Squadrons).Count);

        using Client resumed = fixture.Select(1000001);
        Assert.IsType<PilotCallResult.Success>(resumed.Call("fighterMgr", "RecallFightersToTubes", new List<object?> { item }));
        for (int tick = 0; tick < 20 && deck.Read(pilot.PilotId, pilot.ShipId).Squadrons.Count > 0; tick++)
        {
            fixture.Clock.Seconds += 1;
            await resumed.TickAsync();
        }
        Assert.Empty(deck.Read(pilot.PilotId, pilot.ShipId).Squadrons);
        var landed = Assert.Single(deck.Read(pilot.PilotId, pilot.ShipId).Tubes);
        Assert.Equal(item, landed.SquadronId);
        Assert.Equal(5, landed.Count);
        Assert.Contains(resumed.Packets, packet => ContainsText(packet, "OnFighterRemovedFromController"));
        long replacement = fixture.PutInFighterBay(Templar, 1);
        Assert.IsType<PilotCallResult.Success>(resumed.Call("fighterMgr", "LoadFightersToTube", replacement, tube));
        Assert.Equal(6, Assert.Single(deck.Read(pilot.PilotId, pilot.ShipId).Tubes).Count);
    }
}
