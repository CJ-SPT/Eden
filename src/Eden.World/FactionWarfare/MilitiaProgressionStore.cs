using System.Text.Json;
using Eden.Data.World.Alliances;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Gameplay.FactionWarfare;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.FactionWarfare;

[Injectable(InjectionType.Singleton)]
internal sealed class MilitiaProgressionStore(WorldDatabase database, TimeProvider clock) : IMilitiaProgression
{
    private static readonly MilitiaFaction[] FactionsInWar = Enum.GetValues<MilitiaFaction>();
    private const int DaysInWeek = 7;

    private sealed record DayActivity(long Day, MilitiaActivity Activity);

    private sealed record Member(int Corporation, MilitiaFaction Faction);

    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Militia progression requires the resident world.");
    private long Today => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() / TimeSpan.TicksPerDay;

    private static MilitiaRank ReadRank(WorldAction world, long pilotId, MilitiaFaction faction) =>
        new(pilotId, faction, MilitiaProgressionRules.Rank(Standing(world, pilotId, MilitiaRules.Corporation(faction))));

    public MilitiaRank? Rank(long pilotId) =>
        LiveWorld
            .Find(database.Path)
            ?.Economy.Inspect(world =>
                world.Pilots.Find(pilotId) is { } pilot && MilitiaEnlistmentStore.Effective(world, pilot) is { } faction
                    ? ReadRank(world, pilotId, faction)
                    : null
            );

    public IReadOnlyList<MilitiaRank> Ranks(long pilotId) =>
        LiveWorld
            .Find(database.Path)
            ?.Economy.Inspect(world =>
                FactionsInWar.Select(faction => ReadRank(world, pilotId, faction)).Where(rank => rank.Rank > 0).ToArray()
            )
        ?? [];

    public MilitiaStatistics Statistics(long pilotId, MilitiaStatisticOwner owner)
    {
        long today = Today;
        return Resident.Economy.Inspect(world =>
        {
            if (world.Pilots.Find(pilotId) is not { } pilot)
            {
                return default;
            }
            long id = owner switch
            {
                MilitiaStatisticOwner.Pilot => pilotId,
                MilitiaStatisticOwner.Corporation => pilot.CorporationId,
                MilitiaStatisticOwner.Alliance => Alliance(world, pilot) ?? 0,
                MilitiaStatisticOwner.Faction => (int?)MilitiaEnlistmentStore.Effective(world, pilot) ?? 0,
                _ => throw new ArgumentOutOfRangeException(nameof(owner)),
            };
            return Read(world.Set<SavedMilitiaStatistics>().Find((int)owner, id), today);
        });
    }

    public IReadOnlyList<MilitiaLeaderboard> Leaderboards()
    {
        long today = Today;
        return Resident.Economy.Inspect(world =>
        {
            var groups = world.Set<SavedMilitiaStatistics>().ToArray().ToLookup(row => row.Scope);
            return new[] { MilitiaStatisticOwner.Pilot, MilitiaStatisticOwner.Corporation, MilitiaStatisticOwner.Alliance }
                .Select(owner =>
                {
                    MilitiaStatistics top = default;
                    MilitiaStatistics all = default;
                    foreach (var row in groups[owner])
                    {
                        var stats = Read(row, today);
                        top = new(Max(top.Yesterday, stats.Yesterday), Max(top.LastWeek, stats.LastWeek), Max(top.Total, stats.Total));
                        all += stats;
                    }
                    return new MilitiaLeaderboard(owner, top, all);
                })
                .ToArray();
        });

        static MilitiaActivity Max(MilitiaActivity first, MilitiaActivity second) =>
            new(Math.Max(first.Kills, second.Kills), Math.Max(first.VictoryPoints, second.VictoryPoints));
    }

    public IReadOnlyList<MilitiaFactionStatistics> Factions()
    {
        long today = Today;
        return Resident.Economy.Inspect(world =>
        {
            var ownership = FactionWarzoneCatalog.Roster.ToLookup(system => FactionWarzoneStore.Occupier(world, system.SystemId));
            var members = world
                .Pilots.AsEnumerable()
                .Where(pilot => world.Set<SavedCharacterDeletion>().Find(pilot.Id)?.DeletedAt is null)
                .Select(pilot => (pilot.CorporationId, Faction: MilitiaEnlistmentStore.Effective(world, pilot)))
                .Where(pilot => pilot.Faction.HasValue)
                .Select(pilot => new Member(pilot.CorporationId, pilot.Faction!.Value))
                .ToLookup(pilot => pilot.Faction);
            return FactionsInWar
                .Select(faction =>
                {
                    var corporations = members[faction]
                        .GroupBy(pilot => pilot.Corporation)
                        .Where(corp =>
                            world.Set<SavedCorporation>().Find(corp.Key) is { ClosedAt: null }
                            && MilitiaOrganizationStore.CorporationFaction(world, corp.Key) == faction
                        )
                        .OrderByDescending(corp => corp.Count())
                        .ThenBy(corp => corp.Key)
                        .ToArray();
                    return new MilitiaFactionStatistics(
                        faction,
                        ownership[faction].Count(),
                        members[faction].Count(),
                        members[faction].Count(pilot => pilot.Corporation == MilitiaRules.Corporation(faction)),
                        corporations.Length,
                        corporations.FirstOrDefault()?.Key ?? 0,
                        corporations.FirstOrDefault()?.Count() ?? 0,
                        Read(world.Set<SavedMilitiaStatistics>().Find((int)MilitiaStatisticOwner.Faction, (int)faction), today)
                    );
                })
                .ToArray();
        });
    }

    public int CorporationPilots(long pilotId) =>
        Resident.Economy.Inspect(world =>
        {
            var pilot = world.Pilots.Find(pilotId);
            if (
                pilot is null
                || world.Set<SavedCharacterDeletion>().Find(pilotId)?.DeletedAt is not null
                || MilitiaEnlistmentStore.Effective(world, pilot) is not { } faction
            )
            {
                return 0;
            }
            return world
                .Filter<Pilot>(member => member.CorporationId == pilot.CorporationId)
                .AsEnumerable()
                .Where(member => world.Set<SavedCharacterDeletion>().Find(member.Id)?.DeletedAt is null)
                .Count(member => MilitiaEnlistmentStore.Effective(world, member) == faction);
        });

    private static int? Alliance(WorldAction world, Pilot pilot) => world.Set<SavedAllianceMember>().Find(pilot.CorporationId)?.AllianceId;

    internal static void Record(WorldAction world, Pilot pilot, MilitiaFaction faction, MilitiaActivity increment, long now)
    {
        if (increment == default)
        {
            return;
        }
        long day = now / TimeSpan.TicksPerDay;
        Award(MilitiaStatisticOwner.Pilot, pilot.Id);
        Award(MilitiaStatisticOwner.Corporation, pilot.CorporationId);
        if (Alliance(world, pilot) is int alliance)
        {
            Award(MilitiaStatisticOwner.Alliance, alliance);
        }
        Award(MilitiaStatisticOwner.Faction, (int)faction);

        void Award(MilitiaStatisticOwner owner, long id)
        {
            var row =
                world.Set<SavedMilitiaStatistics>().Find((int)owner, id)
                ?? world.Add(new SavedMilitiaStatistics { Scope = owner, OwnerId = id }).Entity;
            var recent = Days(row).ToDictionary(entry => entry.Day, entry => entry.Activity);
            recent[day] = recent.GetValueOrDefault(day) + increment;
            row.Kills = checked(row.Kills + increment.Kills);
            row.VictoryPoints = checked(row.VictoryPoints + increment.VictoryPoints);
            long cutoff = recent.Keys.Max() - DaysInWeek;
            row.DaysJson = JsonSerializer.Serialize(
                recent
                    .Where(entry => entry.Key >= cutoff)
                    .OrderBy(entry => entry.Key)
                    .Select(entry => new DayActivity(entry.Key, entry.Value))
            );
        }
    }

    private static DayActivity[] Days(SavedMilitiaStatistics row) =>
        JsonSerializer.Deserialize<DayActivity[]>(row.DaysJson) ?? throw new InvalidDataException("Invalid militia statistics.");

    private static MilitiaStatistics Read(SavedMilitiaStatistics? row, long today)
    {
        if (row is null)
        {
            return default;
        }
        var days = Days(row);
        return new(
            days.SingleOrDefault(day => day.Day == today - 1)?.Activity ?? default,
            days.Where(day => day.Day >= today - DaysInWeek && day.Day < today)
                .Aggregate(default(MilitiaActivity), (sum, day) => sum + day.Activity),
            new(row.Kills, row.VictoryPoints)
        );
    }

    internal static double Standing(WorldAction world, long pilot, long source) =>
        world.Set<SavedNpcStanding>().Find(pilot, source)?.Value ?? 0;

    internal static void AdjustStanding(
        WorldAction world,
        Pilot pilot,
        MilitiaFaction faction,
        double fraction,
        StandingEvent reason,
        int system,
        long now,
        List<MissionStandingChange>? changes = null
    )
    {
        int issuer = MilitiaRules.Corporation(faction);
        var standing =
            world.Set<SavedNpcStanding>().Find(pilot.Id, (long)issuer)
            ?? world.Add(new SavedNpcStanding { PilotId = pilot.Id, SourceId = issuer }).Entity;
        int before = MilitiaProgressionRules.Rank(standing.Value);
        Apply(world, pilot, standing, fraction, reason, issuer, system, now, changes);
        int rank = MilitiaProgressionRules.Rank(standing.Value);
        var history = world.Set<SavedMilitiaRank>().Find(pilot.Id, (int)faction);
        int highest = Math.Max(before, history?.HighestRank ?? 0);
        if (highest > 0 || rank > 0)
        {
            history ??= world.Add(new SavedMilitiaRank { PilotId = pilot.Id, Faction = faction }).Entity;
            history.HighestRank = Math.Max(rank, highest);
        }
        if (rank <= highest)
        {
            return;
        }

        var empire =
            world.Set<SavedNpcStanding>().Find(pilot.Id, (long)faction)
            ?? world.Add(new SavedNpcStanding { PilotId = pilot.Id, SourceId = (int)faction }).Entity;
        for (int promotion = highest; promotion < rank; promotion++)
        {
            Apply(
                world,
                pilot,
                empire,
                MilitiaProgressionRules.PromotionFactionFraction,
                StandingEvent.PromotionFactionIncrease,
                issuer,
                system,
                now,
                changes
            );
        }
    }

    internal static void Apply(
        WorldAction world,
        Pilot pilot,
        SavedNpcStanding standing,
        double fraction,
        StandingEvent reason,
        int corporation,
        int system,
        long now,
        List<MissionStandingChange>? changes,
        int victimType = 0
    )
    {
        double before = standing.Value;
        double after = fraction >= 0 ? MissionRules.Gain(before, fraction) : StandingRules.FactionLoss(before, -fraction);
        if (after == before)
        {
            return;
        }
        standing.Value = after;
        world.Add(
            new SavedStandingTransaction
            {
                PilotId = pilot.Id,
                SourceId = standing.SourceId,
                Time = now,
                Event = reason,
                Before = before,
                After = after,
                VictimCorporationId = corporation,
                SystemId = system,
                VictimTypeId = victimType,
            }
        );
        changes?.Add(new(standing.SourceId, before, after));
    }
}
