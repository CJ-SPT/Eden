using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;

namespace Eden.Protocol.Tests.Combat;

public sealed class NpcSceneUpdateTests
{
    [Fact]
    public void ValueFilteringRetainsBoundaryContactsOrderingAndOffGridWarpTracking()
    {
        using NpcCombatTests.Fixture f = new(useTestClock: true);
        var pilot = f.Pilot with { Position = SpacePosition.Zero };
        var boundary = f.Npc with { Position = new(NpcEncounterDefinitions.VisibilityRange, 0, 0) };
        var inside = boundary with { Id = boundary.Id + 1, Position = SpacePosition.Zero };
        var outside = boundary with { Id = boundary.Id + 2, Position = new(NpcEncounterDefinitions.VisibilityRange + 1, 0, 0) };
        var warping = outside with { Id = boundary.Id + 3, Warp = new(SpacePosition.Zero, outside.Position, 1, pilot.Flight, 1) };
        var elsewhere = inside with { Id = boundary.Id + 4, SystemId = pilot.SystemId + 1, Warp = warping.Warp };
        Encounters encounters = new([outside, inside, elsewhere, boundary, warping]);
        List<DestinyCommand> commands = [];
        FlightNpcScene scene = new(new(), f.Clock, encounters, new(commands.Add), commands.Add, _ => { });
        FlightSceneContext context = new(pilot, true, true, 1);
        scene.Capture(context);
        Assert.Equal(new[] { inside.Id, boundary.Id }, scene.Contacts.Select(n => n.Id));
        Assert.True(scene.InWarp(warping.Id));
        Assert.False(scene.InWarp(elsewhere.Id));
        scene.RefreshNpcs(context);
        Assert.Empty(commands);
        scene.Capture(context with { Pilot = pilot with { Position = null } });
        Assert.Empty(scene.Contacts);
        Assert.False(scene.InWarp(warping.Id));
    }

    private sealed class Encounters(IReadOnlyList<NpcContact> contacts) : INpcEncounters
    {
        internal IReadOnlyList<NpcContact> Current { get; set; } = contacts;

        public IReadOnlyList<NpcContact> List(int systemId, long now) => Current;

        public bool WasDestroyed(long npcId) => false;
    }

    [Fact]
    public void UnchangedObserverScenesRetainTheirSnapshotWithBoundedAllocations()
    {
        using NpcCombatTests.Fixture f = new();
        const int count = 2000;
        Encounters encounters = new(Enumerable.Range(0, count).Select(index => f.Npc with { Id = f.Npc.Id + index }).ToArray());
        List<DestinyCommand> commands = [];
        FlightNpcScene scene = new(new(), f.Clock, encounters, new(commands.Add), commands.Add, _ => { });
        FlightSceneContext context = new(f.Pilot, true, true, 1);
        scene.Capture(context);
        var original = scene.Contacts;
        scene.RefreshNpcs(context);
        long before = GC.GetAllocatedBytesForCurrentThread();
        const int repeats = 20;
        for (int index = 0; index < repeats; index++)
        {
            scene.RefreshNpcs(context);
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, repeats * count * 128L);
        Assert.Same(original, scene.Contacts);
        Assert.Empty(commands);
    }

    [Fact]
    public void ShotUpdatesPreserveSnapshotsEffectsAndIdentityAcrossSceneReplacement()
    {
        using NpcCombatTests.Fixture f = new();
        var first = f.Npc with { Weapon = null };
        var second = first with { Id = first.Id + 1, Health = first.Health with { ShipId = first.Id + 1 } };
        Encounters encounters = new([first, second]);
        List<DestinyCommand> updates = [];
        FlightNpcScene scene = new(new(), f.Clock, encounters, new(updates.Add), updates.Add, _ => { });
        FlightSceneContext context = new(f.Pilot, true, true, 1);
        scene.Capture(context);
        var original = scene.Contacts;
        NpcWeaponContact weapon = new(NpcCombatTests.Fixture.ShipId, 562, f.Clock.Now, 1000);
        scene.NotifyNpcWeapon(first.Id, weapon);
        var paid = scene.Contacts;
        scene.NotifyNpcWeapon(first.Id, weapon with { StartedAt = weapon.StartedAt + 1 });
        var effects = updates.OfType<DestinyCommand.TimedSpecialEffect>().ToArray();
        Assert.Equal(new[] { true, false, true }, effects.Select(e => e.Start));
        Assert.All(effects, effect => Assert.Equal(first.Id, effect.Id));
        Assert.Null(original[0].Weapon);
        Assert.Equal(weapon, paid[0].Weapon);
        var health = first.Health with { Remaining = first.Health.Remaining with { Shield = 1 } };
        scene.NotifyHealth(health);
        Assert.Equal(health, scene.Contacts[0].Health);
        Assert.Equal(first.Health, original[0].Health);
        var current = scene.Contacts;
        scene.NotifyHealth(health with { ShipId = NpcCombatTests.Fixture.ShipId });
        Assert.Same(current, scene.Contacts);
        Assert.Equal(NpcCombatTests.Fixture.ShipId, Assert.IsType<DestinyCommand.OnDamageStateChange>(updates[^1]).Id);
        encounters.Current = [second, scene.Contacts[0]];
        updates.Clear();
        scene.RefreshNpcs(context);
        Assert.Empty(updates.OfType<DestinyCommand.TimedSpecialEffect>());
        scene.NotifyNpcWeapon(first.Id, weapon);
        Assert.Equal(weapon, scene.Contacts[1].Weapon);
        Assert.Null(scene.Contacts[0].Weapon);
        scene.StopNpcWeapons(NpcCombatTests.Fixture.ShipId);
        Assert.All(scene.Contacts, npc => Assert.Null(npc.Weapon));
        Assert.Equal(weapon, paid[0].Weapon);
        encounters.Current = [second];
        scene.RefreshNpcs(context);
        Assert.Equal(second, Assert.Single(scene.Contacts));
        current = scene.Contacts;
        scene.NotifyNpcWeapon(first.Id, weapon);
        Assert.Same(current, scene.Contacts);
        scene.Clear();
        Assert.Empty(scene.Contacts);
        scene.NotifyHealth(health);
        Assert.Empty(scene.Contacts);
        scene.Capture(context);
        scene.NotifyNpcWeapon(second.Id, weapon);
        Assert.Equal(weapon, Assert.Single(scene.Contacts).Weapon);
    }

    [Fact]
    public void UpdatingEveryNpcDoesNotCopyTheCompleteSceneForEachShot()
    {
        using NpcCombatTests.Fixture f = new();
        const int count = 2000;
        var contacts = Enumerable
            .Range(0, count)
            .Select(index => f.Npc with { Id = f.Npc.Id + index, Weapon = null, Health = f.Npc.Health with { ShipId = f.Npc.Id + index } })
            .ToArray();
        Encounters encounters = new(contacts);
        FlightNpcScene scene = new(new(), f.Clock, encounters, new(_ => { }), _ => { }, _ => { });
        scene.Capture(new(f.Pilot, true, true, 1));
        var original = scene.Contacts;
        NpcWeaponContact weapon = new(NpcCombatTests.Fixture.ShipId, 562, f.Clock.Now, 1000);
        scene.NotifyNpcWeapon(contacts[0].Id, weapon);
        var playerHealth = f.Npc.Health with { ShipId = NpcCombatTests.Fixture.ShipId };
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var npc in contacts)
        {
            scene.NotifyNpcWeapon(npc.Id, weapon);
            scene.NotifyHealth(playerHealth);
            scene.NotifyHealth(npc.Health with { Remaining = npc.Health.Remaining with { Shield = 1 } });
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, count * 8192L);
        Assert.All(original, npc => Assert.Null(npc.Weapon));
        Assert.All(
            scene.Contacts,
            npc =>
            {
                Assert.Equal(weapon, npc.Weapon);
                Assert.Equal(1, npc.Health.Remaining.Shield);
            }
        );
        Assert.Equal(contacts.Select(n => n.Id), scene.Contacts.Select(n => n.Id));
    }
}
