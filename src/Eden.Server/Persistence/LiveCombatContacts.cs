using Eden.Gameplay.Combat;

namespace Eden.Server.Persistence;

internal sealed partial class LiveCombatRuntime
{
    private sealed record ContactSnapshot(long Second, int Motion, long Epoch, long At, IReadOnlyList<NpcContact> Values);

    private readonly Dictionary<int, ContactSnapshot> _contacts = [];
    private long _contactEpoch;

    private sealed record AcquisitionSnapshot(NpcTargetCandidate[] Candidates, long At);

    private const int RetainedAcquisitions = 128;
    private readonly List<AcquisitionSnapshot> _acquisitions = [];

    internal IReadOnlyList<NpcContact>? Contacts(
        int system,
        long now,
        Func<IReadOnlyList<NpcContact>> read,
        Func<long>? nextPopulation = null
    )
    {
        using var ownership = owner.Enter();
        if (!CanRun)
        {
            return null;
        }
        Trim();
        if (simulationObjects)
        {
            return SimulationContacts(system, now, () => read(), nextPopulation ?? (() => long.MaxValue));
        }
        long second = now / TimeSpan.TicksPerSecond;
        int motion = database.MotionStamps.GetValueOrDefault(system);
        if (
            !_contacts.TryGetValue(system, out var cached)
            || cached.Second != second
            || cached.Motion != motion
            || cached.Epoch != _contactEpoch
        )
        {
            Flush();
            // Contact construction can seed, retire or return NPCs. Preserve those
            // committed invalidations rather than treating it as hydration only.
            var rows = read();
            cached = new(second, motion, _contactEpoch, now, rows);
            _contacts[system] = cached;
        }
        return cached
            .Values.Select(contact =>
            {
                var repaired = Npc(contact.Id, now);
                var health =
                    _npcs.TryGetValue(contact.Id, out var npc) && !_staleNpcs.Contains(contact.Id)
                        ? new ShipHealth(contact.Id, npc.Health.Defense, npc.Health.At(now))
                        : contact.Health with
                        {
                            Remaining = contact.Health.Remaining with
                            {
                                Shield = Eden.Gameplay.Ships.ChargedResource.Recharged(
                                    contact.Health.Remaining.Shield,
                                    contact.Health.Defense.Shield,
                                    contact.Health.Defense.ShieldRechargeMilliseconds,
                                    Math.Max(0, (now - cached.At) / (double)TimeSpan.TicksPerMillisecond)
                                ),
                            },
                        };
                var weapon =
                    contact.Weapon is { } active && now - active.StartedAt >= active.Duration * TimeSpan.TicksPerMillisecond
                        ? null
                        : contact.Weapon;
                if (
                    _aggressions is not null
                    && _aggressions.TryGetValue(contact.Id, out var aggression)
                    && _npcs.TryGetValue(contact.Id, out var source)
                    && !_staleNpcs.Contains(contact.Id)
                )
                {
                    double duration = source.Attributes.GetValueOrDefault(Eden.Gameplay.Dogma.DogmaAttributeId.WeaponCycleTime);
                    weapon =
                        aggression.LastShotAt is { } started && now >= started && now - started < duration * TimeSpan.TicksPerMillisecond
                            ? new(
                                aggression.ShipId,
                                checked((int)source.Attributes.GetValueOrDefault(Eden.Gameplay.Dogma.DogmaAttributeId.EntityTurretType)),
                                started,
                                duration
                            )
                            : null;
                }
                return contact with
                {
                    Health = health,
                    Weapon = weapon,
                    RepairRevision = repaired?.RepairState.Revision ?? contact.RepairRevision,
                };
            })
            .ToArray();
    }

    internal bool Acquire(IReadOnlyList<NpcTargetCandidate> candidates, long now, Action acquire)
    {
        using var ownership = owner.Enter();
        if (!CanRun)
        {
            return false;
        }
        _acquisitions.RemoveAll(s => now < s.At || now - s.At >= TimeSpan.TicksPerSecond);
        if (_acquisitions.Any(s => s.Candidates.SequenceEqual(candidates)))
        {
            return true;
        }
        if (simulationObjects)
        {
            SimulationAcquire(candidates, now);
        }
        else
        {
            acquire();
        }
        if (_acquisitions.Count >= RetainedAcquisitions)
        {
            _acquisitions.RemoveAt(0);
        }
        _acquisitions.Add(new(candidates.ToArray(), now));
        return true;
    }
}
