using Eden.Gameplay.Combat;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    private sealed record SharedNpcContact(
        LiveCombatNpc Source,
        LiveAggression? Aggression,
        int Stamp,
        bool Bumped,
        long Epoch,
        long From,
        long Until,
        NpcContact Contact
    );

    private readonly Dictionary<long, SharedNpcContact> _sharedNpcContacts = [];

    // Recharging, timed patrols, warps and mining stay fresh on every read.
    // Stable contacts expire at the exact weapon deadline, without rounding time.
    private static bool CanShareNpcContact(LiveCombatNpc npc) =>
        npc.Health.Remaining.Shield == npc.Health.Defense.Shield
        && npc.Transit is null
        && npc.Activity is null
        && npc.IncomingElectronicWarfare.Count == 0
        && npc.Navigation is not (NpcNavigationState.Patrolling or NpcNavigationState.Warping);

    private bool TrySharedNpcContact(
        LiveCombatNpc npc,
        LiveAggression? aggression,
        int stamp,
        bool bumped,
        long now,
        out NpcContact contact
    )
    {
        if (
            CanShareNpcContact(npc)
            && _sharedNpcContacts.TryGetValue(npc.Id, out var cached)
            && ReferenceEquals(cached.Source, npc)
            && ReferenceEquals(cached.Aggression, aggression)
            && cached.Stamp == stamp
            && cached.Bumped == bumped
            && cached.Epoch == _contactEpoch
            && now >= cached.From
            && now < cached.Until
        )
        {
            contact = cached.Contact;
            return true;
        }
        contact = null!;
        return false;
    }

    private void ShareNpcContact(LiveCombatNpc npc, LiveAggression? aggression, int stamp, bool bumped, long now, NpcContact contact)
    {
        if (!CanShareNpcContact(npc) || contact.ElectronicWarfare is { Count: > 0 })
        {
            return;
        }
        long until = aggression?.LastShotAt is long future && future > now ? future : long.MaxValue;
        if (contact.Weapon is { } weapon)
        {
            double duration = Math.Ceiling(weapon.Duration * TimeSpan.TicksPerMillisecond);
            if (double.IsFinite(duration) && duration < long.MaxValue - weapon.StartedAt)
            {
                until = weapon.StartedAt + (long)duration;
            }
        }
        if (_sharedNpcContacts.Count >= RetainedNpcs)
        {
            _sharedNpcContacts.Clear();
        }
        _sharedNpcContacts[npc.Id] = new(npc, aggression, stamp, bumped, _contactEpoch, now, until, contact);
    }
}
