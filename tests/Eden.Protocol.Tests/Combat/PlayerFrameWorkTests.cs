using Eden.Data.Combat.Npcs;
using Eden.Data.Corporations;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.World.Combat.Npcs;
using Eden.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class PlayerFrameWorkTests
{
    [Fact]
    public void CorporationChangePollingSkipsSqlUntilCommittedRowsChangeAndKeepsPageBoundaries()
    {
        using TestWorld f = new(liveWorld: true);
        const long pilot = 90000001;
        var membership = f.Services.GetRequiredService<ICorporationMembership>();
        Assert.Empty(membership.Changes(pilot, 0));
        using (var metrics = SessionPerformance.Begin("empty corporation events", _ => { }, force: true)!)
        {
            for (int index = 0; index < 20; index++)
            {
                Assert.Empty(membership.Changes(pilot, 0));
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
        }
        using (WorldDbContext world = new(f.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            world.Add(new SavedCorporationChange { PilotId = pilot });
            world.SaveChanges();
            transaction.Rollback();
        }
        Assert.Empty(membership.Changes(pilot, 0));
        const int remainder = 3;
        using (WorldDbContext world = new(f.Path))
        {
            world.AddRange(
                Enumerable
                    .Range(0, CorporationMembershipRules.ChangePageSize + remainder)
                    .Select(_ => new SavedCorporationChange { PilotId = pilot })
            );
            world.SaveChanges();
        }
        var first = membership.Changes(pilot, 0);
        Assert.Equal(CorporationMembershipRules.ChangePageSize, first.Count);
        var second = membership.Changes(pilot, first[^1].Id);
        Assert.Equal(remainder, second.Count);
        Assert.True(first[^1].Id < second[0].Id);
        Assert.Empty(membership.Changes(90000002, 0));
        using (var metrics = SessionPerformance.Begin("acknowledged corporation events", _ => { }, force: true)!)
        {
            Assert.Empty(membership.Changes(pilot, second[^1].Id));
            Assert.Empty(membership.Changes(90000002, 0));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
        }
        using (WorldDbContext world = new(f.Path))
        {
            world.Add(new SavedCorporationChange { PilotId = pilot });
            world.SaveChanges();
        }
        Assert.True(Assert.Single(membership.Changes(pilot, second[^1].Id)).Id > second[^1].Id);
    }

    [Fact]
    public void StampedNpcContactsShareStableRecordsButDamageAndSubsecondRechargingRemainImmediate()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
        f.Db.Store.Advance(f.Npc.SystemId, 1, f.Clock.Now, [], []);
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        NpcContact Contact(long now) => encounters.List(f.Npc.SystemId, now).Single(n => n.Id == f.Npc.Id);
        var original = Contact(f.Clock.Now);
        using (var metrics = SessionPerformance.Begin("shared observer records", _ => { }, force: true)!)
        {
            Assert.Same(original, Contact(f.Clock.Now + 1));
            Assert.Same(original, Contact(f.Clock.Now + TimeSpan.TicksPerMillisecond));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        f.Aggress();
        var damaged = Contact(f.Clock.Now);
        Assert.True(damaged.Health.Remaining.Shield < original.Health.Remaining.Shield);
        var recharged = Contact(f.Clock.Now + TimeSpan.TicksPerSecond);
        Assert.True(recharged.Health.Remaining.Shield > damaged.Health.Remaining.Shield);
        Assert.Equal(original.Health.Defense.Shield, original.Health.Remaining.Shield);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldNpc>().Find(f.Npc.Id)!.X += 100;
            world.SaveChanges();
        }
        Assert.Equal(damaged.Position.X + 100, Contact(f.Clock.Now).Position.X);
    }

    [Fact]
    public void SharedNpcWeaponsExpireAtTheExactDeadlineAndClockRewindDoesNotKeepTheExpiredView()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
        f.Db.Store.Advance(f.Npc.SystemId, 1, f.Clock.Now, [], []);
        f.Combat.Acquire([new(NpcCombatTests.Fixture.PilotId, NpcCombatTests.Fixture.ShipId)], f.Clock.Now);
        f.Clock.Milliseconds += 20000;
        Assert.NotNull(f.Fire());
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        NpcContact Contact(long now) => encounters.List(f.Npc.SystemId, now).Single(n => n.Id == f.Npc.Id);
        long firedAt = f.Clock.Now;
        var active = Contact(firedAt);
        var weapon = Assert.IsType<NpcWeaponContact>(active.Weapon);
        Assert.Same(active, Contact(firedAt + 1));
        long deadline = firedAt + (long)Math.Ceiling(weapon.Duration * TimeSpan.TicksPerMillisecond);
        Assert.Equal(weapon, Contact(deadline - 1).Weapon);
        Assert.Null(Contact(deadline).Weapon);
        Assert.Equal(weapon, Contact(firedAt + 1).Weapon);
        Assert.NotNull(active.Weapon);
    }

    [Fact]
    public void EmptyPeerEffectsAvoidPerModuleDiffAllocationsAndChangedWeaponsKeepStopStartOrder()
    {
        using NpcCombatTests.Fixture f = new();
        List<DestinyCommand> commands = [];
        FlightEffects effects = new(commands.Add);
        ShipContact empty = new(PilotContact.From(f.Pilot), SpacePosition.Zero, null, 0, null);
        var previous = empty with { Stamp = 1 };
        effects.Replay(empty, previous);
        const int repeats = 2000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < repeats; index++)
        {
            effects.Replay(empty, previous);
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, repeats * 64L);
        Assert.Empty(commands);
        WeaponContact weapon = new(f.Module, 3638, 0, f.Npc.Id, f.Clock.Now, 1000);
        var armed = empty with { Weapons = [weapon] };
        effects.Replay(armed, empty);
        effects.Replay(armed with { Stamp = 2 }, armed);
        effects.Replay(armed with { Weapons = [weapon with { StartedAt = weapon.StartedAt + 1 }] }, armed);
        Assert.Equal(new[] { true, false, true }, commands.OfType<DestinyCommand.TimedSpecialEffect>().Select(c => c.Start));
        Assert.All(commands.OfType<DestinyCommand.TimedSpecialEffect>(), command => Assert.Equal(f.Npc.Id, command.TargetId));
    }
}
