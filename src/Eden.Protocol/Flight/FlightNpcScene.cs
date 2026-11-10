using System.Collections.Frozen;
using System.Collections.Immutable;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Protocol.Combat;

namespace Eden.Protocol.Flight;

internal sealed class FlightNpcScene(
    SharedSpace space,
    TimeProvider clock,
    INpcEncounters? encounters,
    FlightEffects effects,
    Action<DestinyCommand> update,
    Action<StampedDestinyUpdate> send
)
{
    private IReadOnlySet<long> _transitingNpcs = FrozenSet<long>.Empty;

    // A shot changes one contact. Preserve prior snapshots without cloning
    // the complete observer scene for each weapon or health notification.
    private ImmutableList<NpcContact> _contacts = [];
    private readonly Dictionary<long, int> _contactIndices = [];

    internal IReadOnlyList<NpcContact> Contacts => _contacts;

    internal bool InWarp(long id) =>
        _transitingNpcs.Contains(id)
        || _contactIndices.TryGetValue(id, out int index) && _contacts[index].Navigation?.State == NpcNavigationState.Warping;

    internal void Clear()
    {
        _contacts = [];
        _contactIndices.Clear();
        _transitingNpcs = FrozenSet<long>.Empty;
    }

    internal void Capture(FlightSceneContext context) => ReplaceContacts(ReadNpcs(context));

    private void ReplaceContacts(IReadOnlyList<NpcContact> contacts)
    {
        _contacts = contacts.ToImmutableList();
        _contactIndices.Clear();
        for (int index = 0; index < contacts.Count; index++)
        {
            _contactIndices.Add(contacts[index].Id, index);
        }
    }

    private IReadOnlyList<NpcContact> ReadNpcs(FlightSceneContext context)
    {
        var scene = context.Pilot.Position is { } position
            ? encounters?.ReadScene(context.Pilot.SystemId, position, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc())
                ?? NpcSceneContacts.Empty
            : NpcSceneContacts.Empty;
        _transitingNpcs = scene.Warping;
        return scene.Visible;
    }

    internal void RefreshNpcs(FlightSceneContext context)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("contact.npcs");
        if (!context.InBallpark)
        {
            return;
        }
        var current = ReadNpcs(context);
        if (Contacts.Count == current.Count && Contacts.SequenceEqual(current, ReferenceEqualityComparer.Instance))
        {
            return;
        }
        // The identity index addresses the previous ordering. Copy once so a busy refresh
        // can compare every contact in linear time while individual shot updates retain immutable snapshots.
        var previousContacts = _contacts.ToArray();
        if (current.Count != previousContacts.Length || current.Any(n => !_contactIndices.ContainsKey(n.Id)))
        {
            UpdateMembership(current);
        }
        foreach (var npc in current)
        {
            var previous = _contactIndices.TryGetValue(npc.Id, out int index) ? previousContacts[index] : null;
            if (ReferenceEquals(previous, npc))
            {
                continue;
            }
            bool warpChanged = previous is not null && NpcWarpChanged(previous, npc);
            if (previous?.Activity != npc.Activity)
            {
                if (previous?.Activity is { } stoppedActivity)
                {
                    update(NpcActivityWire.Effect(stoppedActivity, false));
                }
                if (npc.Activity is { } startedActivity)
                {
                    update(NpcActivityWire.Effect(startedActivity, true));
                }
            }
            if (!ReferenceEquals(previous?.ElectronicWarfare, npc.ElectronicWarfare))
            {
                foreach (var effect in (previous?.ElectronicWarfare ?? []).Except(npc.ElectronicWarfare ?? []))
                {
                    update(NpcEwarWire.Effect(effect, false));
                }
                foreach (var effect in (npc.ElectronicWarfare ?? []).Except(previous?.ElectronicWarfare ?? []))
                {
                    update(NpcEwarWire.Effect(effect, true));
                }
            }
            if (previous is not null && previous.MaxSpeed != npc.MaxSpeed)
            {
                update(new DestinyCommand.SetMaxSpeed(npc.Id, (double)npc.MaxSpeed));
            }
            if (!warpChanged && previous is not null && npc.Stamp - previous.Stamp > SharedSpace.MovementHistoryTicks)
            {
                FlightEffects.CorrectPosition(
                    send,
                    npc.Id,
                    npc.Position,
                    npc.Navigation?.Velocity ?? SpacePosition.Zero,
                    npc.Stamp == 0 ? context.SimulatedStamp : npc.Stamp
                );
            }
            if (!warpChanged && previous is not null && NpcCourseChanged(previous.Navigation, npc.Navigation))
            {
                ApplyNpcCourse(context, npc);
            }
            if (warpChanged)
            {
                // Native AddBalls reads partial full state even for existing IDs.
                // Replay the timed warp directly: WarpTo would start alignment again.
                // Landing restores position, velocity and solidity in the same update.
                update(new DestinyCommand.AddBalls(Ballpark.AddNpcs([npc], space.Stamp(clock))));
            }
            if (previous?.Warp is not null && npc.Warp is null)
            {
                effects.WarpEffect(npc.Id, false);
            }
            if ((warpChanged || previous is null) && npc.Warp is not null)
            {
                effects.WarpEffect(npc.Id, true);
            }
            if (previous?.Weapon != npc.Weapon)
            {
                if (previous?.Weapon is { } stopped)
                {
                    NpcWeaponEffect(npc.Id, stopped, false);
                }
                if (npc.Weapon is { } started)
                {
                    NpcWeaponEffect(npc.Id, started, true);
                }
            }
            if (
                previous is not null
                && (
                    npc.Health.Remaining.Armor != previous.Health.Remaining.Armor
                    || npc.Health.Remaining.Structure != previous.Health.Remaining.Structure
                    || npc.Health.Remaining.Shield < previous.Health.Remaining.Shield
                    || npc.RepairRevision != previous.RepairRevision
                )
            )
            {
                NotifyHealth(npc.Health);
            }
        }
        ReplaceContacts(current);
    }

    private void UpdateMembership(IReadOnlyList<NpcContact> current)
    {
        foreach (var departed in Contacts.ExceptBy(current.Select(n => n.Id), n => n.Id))
        {
            if (departed.Activity is { } activity)
            {
                update(NpcActivityWire.Effect(activity, false));
            }
            foreach (var effect in departed.ElectronicWarfare ?? [])
            {
                update(NpcEwarWire.Effect(effect, false));
            }
            if (departed.Weapon is { } weapon)
            {
                NpcWeaponEffect(departed.Id, weapon, false);
            }
            if (departed.Warp is not null)
            {
                effects.WarpEffect(departed.Id, false);
            }
            update(new DestinyCommand.RemoveBall(departed.Id, encounters?.WasDestroyed(departed.Id) == true));
        }
        var added = current.Where(n => !_contactIndices.ContainsKey(n.Id)).ToArray();
        if (added.Length > 0)
        {
            update(new DestinyCommand.AddBalls(Ballpark.AddNpcs(added, space.Stamp(clock))));
        }
    }

    private void ApplyNpcCourse(FlightSceneContext context, NpcContact npc)
    {
        int stamp = npc.Stamp == 0 ? context.SimulatedStamp : npc.Stamp;
        void Send(DestinyCommand command) => send(new(stamp, command));
        var course = npc.Navigation;
        if (npc.Warp is { } warp)
        {
            Send(new DestinyCommand.WarpTo(npc.Id, warp.Destination.X, warp.Destination.Y, warp.Destination.Z, 0, warp.WireFactor));
        }
        else if (course is null || course.State == NpcNavigationState.Idle)
        {
            Send(new DestinyCommand.Stop(npc.Id));
        }
        else if (course.TargetId != 0)
        {
            Send(
                new DestinyCommand.Follow(
                    course.State == NpcNavigationState.Orbiting ? NavigationMode.Orbit : NavigationMode.Follow,
                    npc.Id,
                    course.TargetId,
                    course.FollowRange
                )
            );
        }
        else if (course.Destination is { } destination)
        {
            Send(new DestinyCommand.GotoPoint(npc.Id, destination.X, destination.Y, destination.Z));
        }
        Send(new DestinyCommand.SetSpeedFraction(npc.Id, course?.SpeedFraction ?? 0));
    }

    private static bool NpcWarpChanged(NpcContact previous, NpcContact current) =>
        (previous.Warp is null) != (current.Warp is null)
        || previous.Warp?.Destination != current.Warp?.Destination
        || previous.Warp?.Distance != current.Warp?.Distance
        || previous.Warp?.WireFactor != current.Warp?.WireFactor;

    private static bool NpcCourseChanged(NpcNavigationContact? previous, NpcNavigationContact? current) =>
        previous?.State != current?.State
        || previous?.TargetId != current?.TargetId
        || previous?.FollowRange != current?.FollowRange
        || previous?.SpeedFraction != current?.SpeedFraction
        || previous?.Destination != current?.Destination;

    private void NpcWeaponEffect(long npcId, NpcWeaponContact weapon, bool active)
    {
        if (weapon.TurretTypeId <= 0)
        {
            return;
        }
        update(
            new DestinyCommand.TimedSpecialEffect(
                npcId,
                npcId,
                weapon.TurretTypeId,
                weapon.TargetId,
                null,
                "effects.Laser",
                true,
                active,
                active,
                weapon.Duration,
                weapon.StartedAt
            )
        );
    }

    internal void NotifyNpcWeapon(long npcId, NpcWeaponContact weapon)
    {
        if (_contactIndices.TryGetValue(npcId, out int index))
        {
            var npc = _contacts[index];
            if (npc.Weapon is { } previous)
            {
                NpcWeaponEffect(npcId, previous, false);
            }
            NpcWeaponEffect(npcId, weapon, true);
            _contacts = _contacts.SetItem(index, npc with { Weapon = weapon });
            return;
        }
        NpcWeaponEffect(npcId, weapon, true);
    }

    internal void NotifyHealth(ShipHealth current)
    {
        if (_contactIndices.TryGetValue(current.ShipId, out int index))
        {
            _contacts = _contacts.SetItem(index, _contacts[index] with { Health = current });
        }
        update(new DestinyCommand.OnDamageStateChange(current.ShipId, current.DamageState()));
    }

    internal void StopNpcWeapons(long destroyed)
    {
        foreach (var npc in Contacts.Where(n => n.Weapon?.TargetId == destroyed))
        {
            NpcWeaponEffect(npc.Id, npc.Weapon!, false);
        }
        _contacts = _contacts.Select(n => n.Weapon?.TargetId == destroyed ? n with { Weapon = null } : n).ToImmutableList();
    }

    internal void Replay()
    {
        foreach (var npc in Contacts.Where(n => n.Weapon is not null))
        {
            NpcWeaponEffect(npc.Id, npc.Weapon!, true);
        }
        foreach (var effect in Contacts.SelectMany(n => n.ElectronicWarfare ?? []))
        {
            update(NpcEwarWire.Effect(effect, true));
        }
        foreach (var activity in Contacts.Select(n => n.Activity).OfType<Eden.Gameplay.Mining.NpcActivityContact>())
        {
            update(NpcActivityWire.Effect(activity, true));
        }
        foreach (var npc in Contacts.Where(n => n.Warp is not null))
        {
            effects.WarpEffect(npc.Id, true);
        }
    }
}
