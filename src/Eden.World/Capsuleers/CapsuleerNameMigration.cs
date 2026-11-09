using System.Globalization;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal static class CapsuleerNameMigration
{
    private static readonly string[] LegacyGivenNames = ["Ari", "Taren", "Mira", "Kira", "Dara", "Oren", "Jori", "Sera"];
    private static readonly string[] LegacyFamilyNames = ["Voss", "Vartio", "Auvinen", "Kade", "Renn", "Sorin", "Arden", "Neris"];

    internal static void Apply(WorldDbContext world)
    {
        long? seed = world.Set<SavedCapsuleerProvisioning>().Select(s => (long?)s.WorldSeed).SingleOrDefault();
        if (seed is null)
        {
            return;
        }
        var legacyFounders = world.Set<SavedCapsuleerCommunity>().Where(c => c.Id == 1).Select(c => c.PilotId).ToHashSet();
        legacyFounders.UnionWith(world.Set<SavedCapsuleerHauler>().Where(h => h.Id == 1).Select(h => (long?)h.PilotId));
        var candidates = (
            from agent in world.Set<SavedCapsuleerAgent>().AsNoTracking()
            join pilot in world.Pilots.AsNoTracking() on agent.PilotId equals pilot.Id
            where pilot.AccountId == CapsuleerCommunityStore.MinerAccountId
            orderby agent.PilotId
            select new { Agent = agent, pilot.Name }
        )
            .AsEnumerable()
            .Where(c => !legacyFounders.Contains(c.Agent.PilotId) && IsGenerated(c.Name, seed.Value, c.Agent))
            .ToArray();
        if (candidates.Length == 0)
        {
            return;
        }
        // One occupied-name snapshot serves the whole migration, including human
        // pilots. Reserve replacements immediately so collisions remain safe.
        var occupied = world.Pilots.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var agent = candidate.Agent;
            int attempt = 0;
            string name;
            do
            {
                name = CapsuleerProvisioningRules.Name(seed.Value, agent.CommunitySystemId, agent.Career, agent.Slot, attempt++);
            } while (!occupied.Add(name));
            world.Database.ExecuteSql($"UPDATE WorldPilot SET Name={name} WHERE Id={agent.PilotId}");
            // A maintainer's custom ship name is an independent player choice.
            world.Database.ExecuteSql(
                $"""
                UPDATE WorldShip SET Name={name}
                WHERE Id={agent.FounderShipId} AND OwnerId={agent.PilotId} AND Name={candidate.Name}
                """
            );
        }
    }

    private static bool IsGenerated(string name, long seed, SavedCapsuleerAgent agent)
    {
        ulong value = CapsuleerProvisioningRules.Seed(seed, agent.CommunitySystemId, agent.Career);
        string role = agent.Career == CapsuleerCareer.Mining ? "M" : "H";
        string original =
            $"{LegacyGivenNames[value % (ulong)LegacyGivenNames.Length]} "
            + $"{LegacyFamilyNames[(value >> 8) % (ulong)LegacyFamilyNames.Length]} "
            + agent.CommunitySystemId.ToString(CultureInfo.InvariantCulture)
            + role;
        return name == original
            || (
                name.StartsWith(original + "-", StringComparison.Ordinal)
                && int.TryParse(name.AsSpan(original.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int suffix)
                && suffix > 0
            );
    }
}
