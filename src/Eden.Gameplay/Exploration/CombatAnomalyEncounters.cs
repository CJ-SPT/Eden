using System.Collections.Immutable;
using Eden.Gameplay.Combat;

namespace Eden.Gameplay.Exploration;

public sealed record AnomalyNpcGroup(int Minimum, int Maximum, ImmutableArray<int> Types, bool Trigger, bool Optional, bool Sentry);

public sealed record AnomalyEncounterWave(string Heading, ImmutableArray<AnomalyNpcGroup> Groups, int DelaySeconds = 0);

public sealed record AnomalyEncounterReport(
    string Name,
    string Source,
    long Revision,
    string Locations,
    ImmutableArray<ImmutableArray<AnomalyEncounterWave>> Variants,
    bool Reconstructed = false,
    ImmutableDictionary<int, int>? DungeonVariants = null,
    string? LayoutSource = null,
    ImmutableDictionary<int, int>? VersionOneDungeonVariants = null
);

public sealed record AnomalySpawnMember(int TypeId, int Group, bool Trigger, bool Sentry);

public static class AnomalyEncounterRules
{
    public const int CurrentDefinitionVersion = 2;
    public const int SitesPerSystem = 4;
    public const int SentryGroup = NpcEncounterDefinitions.SentryGroup;
    public const int OptionalSpawnOutcomes = 20;
    public const long ObjectsPerSite = 10_000;
    public const long NpcsPerSite = 100_000;
    public const int MembersPerWave = 1000;

    public static bool Allows(string name, double security)
    {
        var band = NpcSpawnRules.SecurityBand(security);
        bool high = band is NpcSecurityBand.Safest or NpcSecurityBand.High;
        bool low = band == NpcSecurityBand.Low;
        if (
            name.Contains("Forsaken Sanctum", StringComparison.Ordinal)
            || name.Contains("Teeming", StringComparison.Ordinal)
            || name.EndsWith("Haven", StringComparison.Ordinal)
            || name.EndsWith("Sanctum", StringComparison.Ordinal)
            || name is "Drone Patrol" or "Drone Horde"
        )
        {
            return !high && !low;
        }
        if (name.EndsWith("Burrow", StringComparison.Ordinal) || name == "Drone Collection")
        {
            return high;
        }
        if (name.EndsWith("Yard", StringComparison.Ordinal) || name == "Drone Surveillance")
        {
            return low;
        }
        if (
            name.EndsWith("Hideaway", StringComparison.Ordinal)
            || name.EndsWith("Refuge", StringComparison.Ordinal)
            || name.EndsWith(" Den", StringComparison.Ordinal)
                && !name.Contains("Hidden", StringComparison.Ordinal)
                && !name.Contains("Forsaken", StringComparison.Ordinal)
                && !name.Contains("Forlorn", StringComparison.Ordinal)
            || name is "Drone Cluster" or "Drone Assembly" or "Drone Gathering"
        )
        {
            return high || low;
        }
        return !high;
    }

    public static IReadOnlyList<AnomalySpawnMember> Members(long siteId, int waveIndex, AnomalyEncounterWave wave)
    {
        List<AnomalySpawnMember> members = [];
        for (int groupIndex = 0; groupIndex < wave.Groups.Length; groupIndex++)
        {
            var group = wave.Groups[groupIndex];
            if (
                group.Minimum < 0
                || group.Maximum < Math.Max(group.Optional ? 1 : 0, group.Minimum)
                || group.Maximum >= MembersPerWave
                || group.Types.IsDefaultOrEmpty
            )
            {
                throw new InvalidDataException("Invalid anomaly NPC group.");
            }
            uint roll = NpcSpawnRules.Seed(waveIndex, siteId + groupIndex);
            // Published reports specify possible spawns without exact probabilities.
            // Eden uses a stable 5% roll for optional NPC groups.
            if (group.Optional && roll % OptionalSpawnOutcomes != 0)
            {
                continue;
            }
            int minimum = group.Optional ? Math.Max(1, group.Minimum) : group.Minimum;
            int count = minimum + (int)(roll % (group.Maximum - minimum + 1));
            for (int slot = 0; slot < count; slot++)
            {
                if (members.Count >= MembersPerWave)
                {
                    throw new InvalidDataException("Anomaly wave exceeds its reserved NPC identities.");
                }
                members.Add(new(group.Types[(int)((roll + slot) % group.Types.Length)], groupIndex, group.Trigger, group.Sentry));
            }
        }
        return members;
    }
}
