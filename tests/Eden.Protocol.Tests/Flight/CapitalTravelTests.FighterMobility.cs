using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Theory]
    [InlineData(Templar, 6, 6, 1, 20000, false)]
    [InlineData(40346, 3, 1, 1, 20000, true)] // Siren afterburner.
    [InlineData(40358, 3, .2, .5, 10000, false)] // Equite evasive maneuvers.
    public async Task FighterSlotOneChangesMobilityAndDefensesForThePinnedCycle(
        int type,
        double speed,
        double signature,
        double shield,
        int duration,
        bool stopEarly
    )
    {
        using Fixture fixture = new(Archon, Helium);
        var source = fixture.Db.Store.FindPilot(1000001)!;
        fixture.Db.Store.SetPosition(source.ShipId, new(1_000_000_000, 0, 0), NullSystem);
        long stack = fixture.PutInFighterBay(type, 3);
        int tube = FighterDeckRules.FirstTube;
        using Client carrier = fixture.Select(1000001);
        using (Client foreign = fixture.Select(1000002))
        {
            var rejected = foreign.Call("fighterMgr", "CmdActivateAbilitySlots", new List<object?> { stack }, 1L);
            Assert.False(Assert.IsType<bool>(Assert.IsType<PilotCallResult.Success>(rejected).Reply.ToBlue()));
        }
        Assert.True(
            Assert.IsType<bool>(
                Assert.IsType<PilotCallResult.Success>(carrier.Call("fighterMgr", "LoadFightersToTube", stack, tube)).Reply.ToBlue()
            )
        );
        Assert.IsType<PilotCallResult.Success>(carrier.Call("fighterMgr", "LaunchFightersFromTubes", new List<object?> { tube }));
        var drones = fixture.Db.Services.GetRequiredService<IPilotDrones>();
        var deck = fixture.Db.Services.GetRequiredService<IPilotFighterDeck>();
        var normal = Contact();
        var started = Activate();
        Assert.True(started is PilotCallResult.Success, started.ToString());
        Assert.IsType<PilotCallResult.Failure>(Activate());
        Assert.Equal(normal.Profile.MaximumSpeed * (float)speed, Contact().Profile.MaximumSpeed);
        Assert.Equal(signature, Contact().MobilitySignatureMultiplier, precision: 8);
        Assert.Equal(normal.Health.Defense.ShieldResonance.Em * shield, Contact().Health.Defense.ShieldResonance.Em, precision: 8);

        Assert.IsType<PilotCallResult.Success>(
            carrier.Call("fighterMgr", "ExecuteMovementCommandOnFighters", new List<object?> { stack }, "ORBIT", source.ShipId, null)
        );
        var cycle = Assert.Single(deck.Read(source.PilotId, source.ShipId).Squadrons).Mobility!;
        Assert.Equal(TimeSpan.FromMilliseconds(duration).Ticks, cycle.EndsAt - cycle.StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(60).Ticks, cycle.ReadyAt - cycle.EndsAt);
        fixture.Clock.Milliseconds += stopEarly ? 1000 : duration - 1;
        await carrier.TickAsync();
        Assert.Contains(carrier.Packets, packet => ContainsText(packet, "OnFighterAbilitySlotActivated"));
        if (stopEarly)
        {
            Assert.IsType<PilotCallResult.Success>(
                carrier.Call("fighterMgr", "CmdDeactivateAbilitySlots", new List<object?> { stack }, 1L)
            );
            cycle = Assert.Single(deck.Read(source.PilotId, source.ShipId).Squadrons).Mobility!;
        }
        else
        {
            Assert.Equal(normal.Profile.MaximumSpeed * (float)speed, Contact().Profile.MaximumSpeed);
            fixture.Clock.Milliseconds++;
        }
        await carrier.TickAsync();
        Assert.Contains(carrier.Packets, packet => ContainsText(packet, "OnFighterAbilitySlotDeactivated"));
        Assert.Equal(normal.Profile.MaximumSpeed, Contact().Profile.MaximumSpeed);
        Assert.Equal(1, Contact().MobilitySignatureMultiplier);
        Assert.Equal(normal.Health.Defense.ShieldResonance, Contact().Health.Defense.ShieldResonance);

        carrier.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.Equal(cycle, Assert.Single(deck.Read(source.PilotId, source.ShipId).Squadrons).Mobility);
        using Client reconnected = fixture.Select(1000001);
        fixture.Clock.Milliseconds += (cycle.ReadyAt - fixture.Clock.Now) / TimeSpan.TicksPerMillisecond - 1;
        Assert.IsType<PilotCallResult.Failure>(Activate(reconnected));
        fixture.Clock.Milliseconds++;
        Assert.IsType<PilotCallResult.Success>(Activate(reconnected));

        DroneContact Contact() => Assert.Single(drones.List(NullSystem, fixture.Clock.Now), d => d.Id == stack);
        PilotCallResult Activate(Client? client = null) =>
            (client ?? carrier).Call("fighterMgr", "CmdActivateAbilitySlots", new List<object?> { stack }, 1L);
    }
}
