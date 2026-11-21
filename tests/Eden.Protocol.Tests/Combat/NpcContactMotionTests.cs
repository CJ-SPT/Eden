using System.Diagnostics;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Combat;

public sealed class NpcContactMotionTests(ITestOutputHelper output)
{
    private const long LastMotion = 50 * TimeSpan.TicksPerSecond;
    private const long Now = LastMotion + 2 * TimeSpan.TicksPerSecond;
    private static readonly IReadOnlyDictionary<DogmaAttributeId, double> s_attributes = new Dictionary<DogmaAttributeId, double>
    {
        [DogmaAttributeId.MaxVelocity] = 200,
        [DogmaAttributeId.Agility] = 0.5,
    };

    [Fact]
    public void UnchangedContactMotionRetainsNpcAndDoesNotAllocate()
    {
        var npc = CreateNpc();
        StaticSpaceObject target = new(2, 0, 0, 0, 0, SpacePosition.Zero, 10);
        (LiveCombatNpc Npc, StaticSpaceObject? Target)[] contacts =
        [
            (npc, null),
            (npc with { Navigation = NpcNavigationState.Patrolling, Role = NpcRole.Navy }, null),
            (npc with { Navigation = NpcNavigationState.Travelling, Role = NpcRole.MiningHauler }, null),
            (npc with { Navigation = NpcNavigationState.Pursuing }, target),
            (npc with { Navigation = NpcNavigationState.Orbiting }, target),
            (npc with { Navigation = NpcNavigationState.Warping }, null),
        ];
        const int repetitions = 8192;
        Assert.Equal(0, SampleContacts(repetitions));
        long started = Stopwatch.GetTimestamp();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int changes = SampleContacts(repetitions);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        output.WriteLine($"{repetitions * contacts.Length:N0} contact samples allocated {allocated:N0} bytes in {elapsed:F2} ms.");
        Assert.Equal(0, changes);
        Assert.Equal(0, allocated);

        int SampleContacts(int count)
        {
            int changed = 0;
            for (int i = 0; i < count; i++)
            {
                foreach (var (subject, locked) in contacts)
                {
                    var result = LiveSystemMotionRules.Move(subject, s_attributes, locked, 0, Now, 0, contactsOnly: true);
                    if (!ReferenceEquals(subject, result))
                    {
                        changed++;
                    }
                }
            }
            return changed;
        }
    }

    [Fact]
    public void ContactMotionRetainsStationaryWarpAndReturnRules()
    {
        var npc = CreateNpc();
        var sentry = npc with { GroupId = NpcEncounterDefinitions.SentryGroup, Navigation = NpcNavigationState.Pursuing };
        Assert.Equal(
            sentry with
            {
                Navigation = NpcNavigationState.Idle,
                Velocity = SpacePosition.Zero,
                MotionAt = Now,
            },
            LiveSystemMotionRules.Move(sentry, s_attributes, null, 0, Now, 0, contactsOnly: true)
        );

        NpcWarpTransit transit = new(npc.Position, new(1e12, 0, 0), LastMotion, Now + TimeSpan.TicksPerSecond * 30, 3);
        var warping = npc with { Navigation = NpcNavigationState.Warping, Transit = transit };
        var sampled = LiveSystemMotionRules.Move(warping, s_attributes, null, 0, Now, 0, contactsOnly: true);
        Assert.Equal(LiveSystemMotionRules.Move(warping, s_attributes, null, 0, Now, 0), sampled);
        Assert.Equal(Now, sampled.MotionAt);
        Assert.Equal(transit, sampled.Transit);

        var returning = npc with { Navigation = NpcNavigationState.Returning };
        var returned = LiveSystemMotionRules.Move(returning, s_attributes, null, 0, Now, 0);
        Assert.Equal(returned, LiveSystemMotionRules.Move(returning, s_attributes, null, 0, Now, 0, contactsOnly: true));
        Assert.Equal(Now, returned.MotionAt);
        Assert.NotEqual(npc.Position, returned.Position);
        foreach (var engaged in new[] { NpcNavigationState.Pursuing, NpcNavigationState.Orbiting })
        {
            Assert.Equal(
                returned,
                LiveSystemMotionRules.Move(npc with { Navigation = engaged }, s_attributes, null, 0, Now, 0, contactsOnly: true)
            );
        }
    }

    private static LiveCombatNpc CreateNpc()
    {
        HullDefense defense = new(10, 10, 10, 100, DamageVector.None, DamageVector.None, DamageVector.None);
        return new(
            1,
            1,
            30000142,
            new(2000, 0, 0),
            new(-25, 0, 0),
            10,
            20,
            new(defense, defense.Undamaged, LastMotion),
            true,
            new Dictionary<long, CombatParty>(),
            NpcRole.Pirate,
            1,
            0,
            1,
            1,
            SpacePosition.Zero,
            s_attributes
        )
        {
            MotionAt = LastMotion,
            Mass = 1_100_000,
        };
    }
}
