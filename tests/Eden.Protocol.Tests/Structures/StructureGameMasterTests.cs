using Eden.Gameplay.Docking;
using Eden.Gameplay.Structures;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Runtime;
using Eden.World.Structures;
using Microsoft.Extensions.DependencyInjection;
using Client = Eden.Protocol.Tests.PilotSessions.CourierServiceTests.Client;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureGameMasterTests
{
    [Fact]
    public void SlashAcceptsEveryNativeStateAndPublishesItToTheSlimItem()
    {
        using Fixture f = new(liveWorld: true);
        var structure = f.Online();
        var contacts = f.Db.Services.GetRequiredService<IStructureContacts>();
        using var gm = Select(f, PilotRole.GameMasterHigh);
        var states = new (int Id, string Name, double Shield, double Armor)[]
        {
            (0, "unknown", 1, 1),
            (1, "unanchored", 1, 1),
            (2, "anchoring", 0, 0),
            (100, "online_deprecated", 1, 1),
            (101, "fitting_invulnerable", 0, 0),
            (102, "onlining_vulnerable", 0, 0),
            (110, "shield_vulnerable", 1, 1),
            (111, "armor_reinforce", 0, 1),
            (112, "armor_vulnerable", 0, 1),
            (113, "hull_reinforce", 0, 0),
            (114, "hull_vulnerable", 0, 0),
            (115, "anchor_vulnerable", 0, 0),
            (116, "deploy_vulnerable", 0, 0),
            (118, "fob_invulnerable", 1, 1),
        };
        foreach (var (id, name, shield, armor) in states)
        {
            Run(gm, $"/structure state {structure.Id} {id}");
            Run(gm, $"/structure state {structure.Id} {name}");
            var result = Assert.Single(contacts.Contacts(Fixture.SystemId), s => s.Id == structure.Id);
            Assert.Equal(id, (int)result.Structure!.State);
            Assert.Equal((long)id, Slim(result)["structureState"]);
            Assert.Equal(shield, result.Structure.DamageFractions!.Shield);
            Assert.Equal(armor, result.Structure.DamageFractions.Armor);
            Assert.Null(result.Structure.TimerEnd);
        }
        Assert.True(Read(f, structure.Id).HasCore);
    }

    [Fact]
    public void SlashRejectsLowerRolesAndInvalidTargetsOrArguments()
    {
        using Fixture f = new(liveWorld: true);
        var structure = f.Online();
        using var gm = Select(f, PilotRole.GameMasterHigh);
        foreach (var role in new[] { PilotRole.Player, PilotRole.GameMasterLow, PilotRole.WorldMod })
        {
            using var caller = Select(f, role);
            Assert.IsType<PilotCallResult.Failure>(Slash(caller, $"/structure timer {structure.Id} 5"));
        }
        foreach (
            string command in new[]
            {
                $"/structure state {structure.Id} 119",
                $"/structure state {structure.Id} invalid",
                $"/structure timer {structure.Id} -1",
                $"/structure timer {structure.Id} {long.MaxValue}",
                $"/structure timer {structure.Id} 5 6",
                $"/structure deploytimer {structure.Id} 5",
                "/structure state 42 110",
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(Slash(gm, command));
        }
        Assert.Null(Read(f, structure.Id).TimerEnd);
        Assert.Equal(StructureState.ShieldVulnerable, Read(f, structure.Id).State);
    }

    [Fact]
    public void OperatingTimerAdvancesAnchoringAndReinforcementAtExactDeadlines()
    {
        using Fixture f = new(liveWorld: true);
        var structure = f.Deploy();
        long id = structure.Id;
        using var gm = Select(f, PilotRole.GameMasterHigh);
        Run(gm, $"/structure timer {id} 5");
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(4));
        Assert.Equal(StructureState.Anchoring, Read(f, id).State);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        Assert.Equal(StructureState.Onlining, Read(f, id).State);
        Run(gm, $"/structure timer {id} 0");
        Assert.Equal(StructureState.Onlining, Read(f, id).State);
        Assert.Null(Read(f, id).TimerEnd);

        Run(gm, $"/structure state {id} 115");
        Run(gm, $"/structure timer {id} 0");
        var anchoring = Read(f, id);
        Assert.Equal(StructureState.Anchoring, anchoring.State);
        Assert.Equal(f.Clock.Now + StructureRules.AnchoringDuration, anchoring.StateEndsAt);

        Run(gm, $"/structure state {id} 111");
        Run(gm, $"/structure timer {id} 15");
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(15));
        var armor = Read(f, id);
        Assert.Equal(StructureState.ArmorVulnerable, armor.State);
        Assert.Equal(f.Clock.Now, armor.StateStartedAt);
        Assert.Equal(0, armor.DamageFractions!.Shield);
        Run(gm, $"/structure state {id} 113");
        Run(gm, $"/structure timer {id} 0");
        var hull = Read(f, id);
        Assert.Equal(StructureState.HullVulnerable, hull.State);
        Assert.Equal(0, hull.DamageFractions!.Armor);
        Run(gm, $"/structure timer {id} 0");
        Assert.Equal(StructureState.ShieldVulnerable, Read(f, id).State);
    }

    [Fact]
    public void AbandonTimerRequiresLowPowerAndResetsWhenServicesOnline()
    {
        using Fixture f = new(liveWorld: true);
        long id = f.Online(35825).Id;
        using var gm = Select(f, PilotRole.GameMasterHigh);
        long module = f.Item(35878);
        f.Structures.FitService(f.PilotId, id, module, StructureRules.FirstServiceSlot);
        f.Structures.DepositFuel(f.PilotId, id, f.Item(4051, 648), 648);
        Run(gm, $"/structure abandontimer {id} 5");
        f.Structures.SetServiceOnline(f.PilotId, id, module, true);
        Assert.IsType<PilotCallResult.Failure>(Slash(gm, $"/structure abandontimer {id} 0"));
        f.Structures.SetServiceOnline(f.PilotId, id, module, false);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(5));
        Assert.Equal(StructureUpkeepState.LowPower, Read(f, id).Upkeep);
        Run(gm, $"/structure abandontimer {id} 0");
        Assert.Equal(StructureUpkeepState.Abandoned, Read(f, id).Upkeep);
        Assert.IsType<PilotCallResult.Failure>(Slash(gm, $"/structure abandontimer {id} 5"));

        Assert.False(StructureRules.CanAbandon(47512));
        Assert.Null(StructureRules.AbandonmentDeadline(47512, f.Clock.Now, f.Clock.Now));
    }

    [Fact]
    public void SlashStartsCancelsAndCompletesUnanchoringThroughResidentEvacuation()
    {
        using Fixture f = new(liveWorld: true);
        var structure = f.Online();
        long id = structure.Id;
        using var gm = Select(f, PilotRole.GameMasterHigh);
        Run(gm, $"/structure unanchor {id} ");
        long deadline = Assert.IsType<long>(Read(f, id).UnanchorAt);
        Run(gm, $"/structure unanchor {id}");
        Assert.Equal(deadline, Read(f, id).UnanchorAt);
        Run(gm, $"/structure unanchor {id} cancel");
        Assert.Null(Read(f, id).UnanchorAt);
        Run(gm, $"/structure unanchor {id} 5");
        Run(gm, $"/structure state {id} 111");
        Assert.Null(Read(f, id).UnanchorAt);
        Assert.IsType<PilotCallResult.Failure>(Slash(gm, $"/structure unanchor {id} 5"));
        Run(gm, $"/structure state {id} 110");

        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, id);
        Run(gm, $"/structure unanchor {id} 5");
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(5));
        Assert.Equal(StructureState.Unanchored, Read(f, id).State);
        Assert.Null(Read(f, id).UnanchorAt);
        Assert.False(f.Db.Store.FindPilot(1000001)!.InStructure);
        Assert.NotNull(f.Db.Store.FindPilot(1000001)!.Position);
    }

    [Fact]
    public async Task DeployRepairAndAbandonTimersRemainIndependentAcrossCheckpoint()
    {
        using Fixture f = new(liveWorld: true);
        long id = f.Online().Id;
        using var gm = Select(f, PilotRole.GameMasterHigh);
        var contacts = f.Db.Services.GetRequiredService<IStructureContacts>();
        Run(gm, $"/structure state {id} 116");
        long start = f.Clock.Now;
        Run(gm, $"/structure deploytimer {id} 5");
        Run(gm, $"/structure abandontimer {id} 15");
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        Run(gm, $"/structure timer {id} 2");
        var slim = Slim(Assert.Single(contacts.Contacts(Fixture.SystemId), s => s.Id == id));
        Assert.Equal(new object?[] { start, start + TimeSpan.FromSeconds(5).Ticks }, Assert.IsType<List<object?>>(slim["deployTimes"]));
        Assert.Equal(new object?[] { f.Clock.Now, start + TimeSpan.FromSeconds(3).Ticks, null }, Assert.IsType<object?[]>(slim["timer"]));

        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        string copy = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "structure-gm.sqlite");
        File.Copy(f.Db.Path, copy);
        using LiveWorld reopened = new(new(copy), f.Clock);
        PilotStructureStore store = new(new(copy), f.Clock);
        Assert.Equal(Read(f, id).LowPowerAt, store.Read(f.PilotId, id)!.LowPowerAt);
        Assert.Equal(slim["deployTimes"], Slim(Assert.Single(store.Contacts(Fixture.SystemId), s => s.Id == id))["deployTimes"]);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(2));
        var repaired = store.Read(f.PilotId, id)!;
        Assert.Equal(StructureState.DeployVulnerable, repaired.State);
        Assert.Null(repaired.TimerEnd);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(2));
        Assert.Equal(StructureState.ShieldVulnerable, store.Read(f.PilotId, id)!.State);
        Assert.Equal(StructureUpkeepState.LowPower, store.Read(f.PilotId, id)!.Upkeep);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(10));
        Assert.Equal(StructureUpkeepState.Abandoned, store.Read(f.PilotId, id)!.Upkeep);
    }

    private static Client Select(Fixture f, PilotRole role)
    {
        // The selected GM belongs to a different corporation from the structure owner.
        Client client = new(f.Db, f.Clock, 1000002, role);
        client.Refresh();
        return client;
    }

    private static PilotCallResult Slash(Client client, string command) => client.Call("SlashCmd", [command], "slash");

    private static void Run(Client client, string command) => Assert.Equal(PilotCallResult.None, Slash(client, command));

    private static StructureSnapshot Read(Fixture f, long id) => f.Structures.Read(f.PilotId, id)!;

    private static Dictionary<string, object?> Slim(Eden.Gameplay.Flight.StaticSpaceObject body) =>
        BlueRead.Fields(Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(new StaticSlimItem(body)))).State);
}
