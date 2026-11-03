using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Propulsion;
using Eden.Protocol.Combat;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.Protocol.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed class FlightBoundaryTests
{
    private sealed class CountingHealth(IPilotHealth inner) : IPilotHealth
    {
        public int Reads { get; private set; }

        public ShipHealth Read(long pilotId, long now)
        {
            Reads++;
            return inner.Read(pilotId, now);
        }

        public bool WasDestroyed(long shipId) => inner.WasDestroyed(shipId);
    }

    private sealed class QuietCombat : INpcCombat
    {
        public int Disengagements { get; private set; }

        public void Acquire(IReadOnlyList<NpcTargetCandidate> candidates, long now) { }

        public IReadOnlyList<long> Aggressors(long shipId) => [];

        public NpcShot? Fire(long pilotId, long shipId, long npcId, SpacePosition velocity, long now, double roll, int destinyStamp) =>
            null;

        public void Disengage(long shipId, long? npcId = null) => Disengagements++;
    }

    [Fact]
    public void SnapshotPublishesTheSameHealthSampleItSerializes()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)! with { Position = SpacePosition.Zero };
        CountingHealth health = new(db.Services.GetRequiredService<IPilotHealth>());
        SharedSpace space = new();
        using FlightSession flight = new(db.Store, pilot, TimeProvider.System, space, [], health: health);
        flight.Snapshot();
        Assert.Equal(1, health.Reads);
        var snapshot = Assert.IsType<DestinyCommand.SetState>(Assert.Single(flight.Updates).Command).Snapshot;
        var peer = Assert.Single(space.Peers(pilot with { ShipId = long.MaxValue }));
        Assert.Equal(pilot.ShipId, snapshot.Ego);
        Assert.Contains(peer.Pilot.ShipId, snapshot.Damage.Keys);
    }

    [Fact]
    public void ProductionFactoryWiresHealthAndDockedTicksDisengageOnlyOnce()
    {
        using TestWorld db = new();
        using var scope = db.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
        var pilot = db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(1000001, pilot.Account, [pilot], factory.Create, 1, SignedSettings.Empty);
        context.Select(pilot);
        QuietCombat combat = new();
        using NpcCombatHandler handler = new(
            TimeProvider.System,
            combat,
            new SharedTargetLocks(),
            scope.ServiceProvider.GetRequiredService<ShipLossCoordinator>(),
            db.Services.GetRequiredService<SharedSpace>()
        );
        handler.Enter(context);
        for (int tick = 0; tick < 100; tick++)
        {
            handler.Advance(context);
        }
        Assert.Equal(1, combat.Disengagements);
        Assert.Same(PilotCallResult.None, context.Flight.Execute(new FlightCommand.Undock(pilot.ShipId)));
        context.Flight.Snapshot();
        var snapshot = Assert.IsType<DestinyCommand.SetState>(Assert.Single(context.Flight.Updates).Command).Snapshot;
        Assert.Contains(pilot.ShipId, snapshot.Damage.Keys);
    }

    [Fact]
    public void EffectReplayPreservesFeatureOrderAndSuppressesUnchangedCycles()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        ShipContact contact = new(
            PilotContact.From(pilot),
            SpacePosition.Zero,
            null,
            0,
            null,
            Mining: [new(11, 1, 99, 1, 1000)],
            Weapons: [new(12, 2, 3, 99, 1, 2000)],
            ShieldBoosts: [new(13, 4, 1, 3000)],
            Propulsion: new(new PropulsionModule(14, 5, 4000, 1, 1, 1, 1), 1)
        );
        List<DestinyCommand> commands = [];
        FlightEffects effects = new(commands.Add);
        effects.Replay(contact, null);
        Assert.Equal(new long?[] { 11, 12, 13, 14 }, commands.Cast<DestinyCommand.TimedSpecialEffect>().Select(c => c.ModuleId));
        Assert.All(commands.Cast<DestinyCommand.TimedSpecialEffect>(), c => Assert.True(c.Start));
        commands.Clear();
        effects.Replay(contact, contact);
        Assert.Empty(commands);
    }
}
