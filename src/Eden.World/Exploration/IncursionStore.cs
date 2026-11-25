using System.Collections.Frozen;
using System.Collections.Immutable;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Corporations;
using Eden.Data.World.Economy;
using Eden.Data.World.Exploration;
using Eden.Data.World.Market;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat.Npcs;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Exploration;

[Injectable(InjectionType.Singleton)]
internal sealed partial class IncursionStore(WorldDatabase database) : IIncursionDirectory, IIncursionMaintenance, IIncursionLoyaltyJournal
{
    private enum RewardType
    {
        Loyalty = 1,
        Isk = 2,
    }

    private enum RewardGroup
    {
        Vanguard = 1,
        Assault = 2,
        Headquarters = 3,
        Scout = 8,
        Mothership = 10,
    }

    private const int AnySecurityRewardTable = 0;

    private sealed record Placement(
        IncursionSecurity Security,
        int ConstellationId,
        int Staging,
        ImmutableArray<int> Vanguards,
        int Assault,
        int Headquarters
    )
    {
        internal ImmutableArray<IncursionSystem> Systems { get; } =
        [
            new(Staging, IncursionScene.Staging),
            .. Vanguards.Select(system => new IncursionSystem(system, IncursionScene.Vanguard)),
            new(Assault, IncursionScene.Assault),
            new(Headquarters, IncursionScene.Headquarters),
        ];
    }

    private sealed record Breakpoint(long PilotCount, long Amount);

    private sealed record PayoutTable(
        RewardGroup Group,
        int LogNameId,
        long MinimumContribution,
        double DamageMultiplier,
        double RepairMultiplier,
        ImmutableArray<Breakpoint> Isk,
        ImmutableArray<Breakpoint> Loyalty
    )
    {
        internal bool Qualifies(SavedIncursionContribution row) =>
            row.Damage + row.Repair > 0 && row.Damage * DamageMultiplier + row.Repair * RepairMultiplier >= MinimumContribution;
    }

    // Observed roles from EVE University. System membership and security are checked against the resident catalog.
    private static readonly Placement[] s_high =
    [
        new(IncursionSecurity.High, 20000566, 30003881, [30003878, 30003880, 30003879], 30003877, 30003876), // Amdimmah
        new(IncursionSecurity.High, 20000604, 30004134, [30004133, 30004132, 30004135], 30004131, 30004130), // Ahrnot
        new(IncursionSecurity.High, 20000209, 30001429, [30001428, 30001426, 30001427], 30001425, 30001424), // Asalola
    ];

    private static readonly Placement[] s_low =
    [
        new(IncursionSecurity.Low, 20000398, 30002721, [30002719, 30002723, 30002720], 30002722, 30002718), // Aeman
        new(IncursionSecurity.Low, 20000620, 30004240, [30004238, 30004241, 30004239], 30004237, 30004236), // Afinoo
        new(IncursionSecurity.Low, 20000572, 30003920, [30003917, 30003919, 30003922], 30003918, 30003921), // Akar
    ];

    private static readonly Placement[] s_null =
    [
        new(IncursionSecurity.Null, 20000042, 30000300, [30000298, 30000295, 30000296], 30000297, 30000299), // 0-8MWQ
        new(IncursionSecurity.Null, 20000117, 30000801, [30000798, 30000800, 30000797], 30000799, 30000796), // 0-YMZM
        new(IncursionSecurity.Null, 20000103, 30000708, [30000707, 30000705, 30000706], 30000703, 30000704), // 02QH-A
        new(IncursionSecurity.Null, 20000472, 30003232, [30003233, 30003231, 30003234], 30003230, 30003229), // 04-LOF
    ];

    private static readonly FrozenDictionary<int, Placement> s_placeBySystem = s_high
        .Concat(s_low)
        .Concat(s_null)
        .SelectMany(place => place.Systems.Select(system => (system.SystemId, Place: place)))
        .ToFrozenDictionary(row => row.SystemId, row => row.Place);

    private static readonly CatalogDefinition<ImmutableDictionary<(RewardGroup, IncursionSecurity), PayoutTable>> s_rewards = new(
        "incursion-site-rewards",
        (catalog, _) =>
            (
                from rewardGroup in Enum.GetValues<RewardGroup>()
                from security in Enum.GetValues<IncursionSecurity>()
                let reward = LoadRewards(catalog, rewardGroup, security)
                where reward is not null
                select (Key: (rewardGroup, security), Reward: reward!)
            ).ToImmutableDictionary(entry => entry.Key, entry => entry.Reward)
    );
    private static readonly IncursionSiteKind[] s_siteKinds = Enum.GetValues<IncursionSiteKind>();
    private long _nextReview;

    private static PayoutTable? Rewards(CatalogDbContext catalog, IncursionSiteKind kind, IncursionSecurity security) =>
        catalog
            .GlobalDefinition(s_rewards)
            .GetValueOrDefault(
                (
                    kind switch
                    {
                        IncursionSiteKind.Scout => RewardGroup.Scout,
                        IncursionSiteKind.Vanguard => RewardGroup.Vanguard,
                        IncursionSiteKind.Assault => RewardGroup.Assault,
                        IncursionSiteKind.Headquarters => RewardGroup.Headquarters,
                        IncursionSiteKind.Mothership => RewardGroup.Mothership,
                        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                    },
                    security
                )
            );

    internal static void PrepareCatalog(CatalogDbContext catalog)
    {
        _ = IncursionSiteCatalog.Variants(catalog, IncursionSiteKind.Vanguard, IncursionSecurity.High);
        _ = catalog.GlobalDefinition(s_rewards);
        IncursionLoot.Prepare(catalog);
    }

    private static Placement ForTale(long taleId)
    {
        long period = IncursionRules.Period(taleId) - 1;
        int lane = IncursionRules.Lane(taleId);
        return lane switch
        {
            IncursionRules.HighSecurityLane => s_high[period % s_high.Length],
            IncursionRules.LowSecurityLane => s_low[period % s_low.Length],
            _ => s_null[(period + lane - IncursionRules.FirstNullSecurityLane) % s_null.Length],
        };
    }

    internal static bool MayHaveSite(int systemId, long now) => TaleInSystem(systemId, now) is not null;

    internal static bool IsPopulationSystem(int systemId) => s_placeBySystem.ContainsKey(systemId);

    internal static long NextPopulationOpening(int systemId, long now) =>
        IsPopulationSystem(systemId) ? IncursionRules.OpensAt(IncursionRules.TaleId(now) + 1) : long.MaxValue;

    private static long? TaleInSystem(int systemId, long now)
    {
        if (!s_placeBySystem.TryGetValue(systemId, out var place))
        {
            return null;
        }
        long primary = IncursionRules.TaleId(now);
        if (now < IncursionRules.OpensAt(primary) || now >= IncursionRules.EndsAt(primary))
        {
            return null;
        }

        int first = place.Security switch
        {
            IncursionSecurity.High => IncursionRules.HighSecurityLane,
            IncursionSecurity.Low => IncursionRules.LowSecurityLane,
            _ => IncursionRules.FirstNullSecurityLane,
        };
        int lanes = place.Security == IncursionSecurity.Null ? IncursionRules.NullSecurityLanes : 1;
        for (int lane = first; lane < first + lanes; lane++)
        {
            long taleId = IncursionRules.TaleId(now, lane);
            if (ReferenceEquals(ForTale(taleId), place))
            {
                return taleId;
            }
        }
        return null;
    }

    internal static IncursionFocus? ActiveFocus(WorldAction world, CatalogDbContext catalog, int systemId, long now) =>
        TaleInSystem(systemId, now) is long taleId ? ResolveFocus(world, catalog, taleId, now) : null;

    private static IncursionFocus? ResolveFocus(WorldAction world, CatalogDbContext catalog, long taleId, long now)
    {
        long opens = IncursionRules.OpensAt(taleId);
        long expires = IncursionRules.EndsAt(taleId);
        if (now < opens || now >= expires)
        {
            return null;
        }

        var progress = world.Set<SavedIncursionCampaign>().Find(taleId);
        if (progress?.DefeatedAt is not null)
        {
            return null;
        }
        var place = ForTale(taleId);
        if (
            place.Systems.Any(system =>
                catalog.FindSystem(system.SystemId)?.ConstellationId != place.ConstellationId
                || Security(catalog.SystemPolitics(system.SystemId).Security) != place.Security
            )
            || s_siteKinds.Any(kind =>
                IncursionSiteCatalog.Variants(catalog, kind, place.Security).Count == 0 || Rewards(catalog, kind, place.Security) is null
            )
        )
        {
            return null;
        }

        long finalAge = IncursionRules.FinalAgeAt(taleId, place.Security);
        return new(
            taleId,
            place.Staging,
            place.ConstellationId,
            progress?.LastUpdated ?? opens,
            expires,
            IncursionRules.Influence(progress?.Clears ?? 0),
            place.Systems,
            FinalAvailable(progress, place.Security, now),
            place.Security,
            now < finalAge ? finalAge : null
        );
    }

    private static IncursionSecurity? Security(double? security) =>
        security switch
        {
            >= 0.45 => IncursionSecurity.High,
            > 0 => IncursionSecurity.Low,
            <= 0 => IncursionSecurity.Null,
            _ => null,
        };

    private static bool FinalAvailable(SavedIncursionCampaign? campaign, IncursionSecurity security, long now) =>
        campaign is { DefeatedAt: null }
        && now >= IncursionRules.FinalAgeAt(campaign.TaleId, security)
        && IncursionRules.FinalAvailable(campaign.Clears, campaign.HeadquartersCleared);

    private LiveWorld Live =>
        LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Incursions require the resident world.");

    public IReadOnlyList<IncursionFocus> Read(long now)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return [];
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return live.Economy.Inspect(world =>
        {
            List<IncursionFocus> active = [];
            for (int lane = 0; lane < IncursionRules.CampaignLanes; lane++)
            {
                var focus = ResolveFocus(world, catalog, IncursionRules.TaleId(now, lane), now);
                live.SetIncursionFocus(lane, focus, now);
                if (focus is not null)
                {
                    active.Add(focus);
                }
            }
            return active.ToArray();
        });
    }

    public IReadOnlyList<IncursionAgencyUpdate> AgencyUpdates => LiveWorld.Find(database.Path)?.IncursionAgencyUpdates ?? [];

    public IReadOnlyList<IncursionPendingLoyalty> PendingLoyalties(long pilotId, long now)
    {
        if (now >= IncursionRules.EndsAt(IncursionRules.TaleId(now)) || LiveWorld.Find(database.Path) is not { } live)
        {
            return [];
        }

        return live.Economy.Inspect(world =>
        {
            List<IncursionPendingLoyalty> pending = [];
            for (int lane = 0; lane < IncursionRules.CampaignLanes; lane++)
            {
                long taleId = IncursionRules.TaleId(now, lane);
                long points = world
                    .Filter<SavedIncursionContribution>(row => row.TaleId == taleId)
                    .Where(row => row.PilotId == pilotId)
                    .Sum(row => row.PendingLoyalty);
                if (points > 0)
                {
                    pending.Add(new(taleId, points));
                }
            }
            return pending.ToArray();
        });
    }

    internal static void Track(WorldAction world, IncursionFocus focus)
    {
        if (world.Set<SavedIncursionCampaign>().Find(focus.TaleId) is null)
        {
            world.Add(new SavedIncursionCampaign { TaleId = focus.TaleId, LastUpdated = focus.LastUpdated });
        }
    }

    public void Advance(long now)
    {
        if (now < Volatile.Read(ref _nextReview))
        {
            return;
        }
        var live = Live;
        var expired = live.Economy.Inspect(world =>
            world
                .Set<SavedIncursionCampaign>()
                .AsEnumerable()
                .Where(campaign => now >= IncursionRules.EndsAt(campaign.TaleId))
                .Select(campaign => campaign.TaleId)
                .ToArray()
        );
        foreach (long taleId in expired)
        {
            using var world = live.Economy.Begin();
            if (world.Set<SavedIncursionCampaign>().Find(taleId) is not { } campaign)
            {
                continue;
            }

            CombatAnomalyStore.RetireIncursion(world, taleId, now);
            IncursionRoamingPopulation.Retire(world, ForTale(taleId).Systems, taleId, now);
            var contributions = world.Filter<SavedIncursionContribution>(row => row.TaleId == taleId).ToArray();
            foreach (var (pilotId, points) in HeldLoyalty(world, contributions))
            {
                RecordLoyaltyClose(
                    world,
                    pilotId,
                    taleId,
                    ForTale(taleId).Staging,
                    IncursionLoyaltyEvent.Forfeited,
                    points,
                    IncursionRules.EndsAt(taleId)
                );
            }
            world.RemoveRange(contributions);
            world.RemoveRange(world.Filter<SavedIncursionRepairDamage>(row => row.TaleId == taleId).ToArray());
            world.Remove(campaign);
            world.Commit();
        }
        _ = Read(now);
        long taleIdNow = IncursionRules.TaleId(now);
        long ends = IncursionRules.EndsAt(taleIdNow);
        long lowFinalAge = IncursionRules.FinalAgeAt(IncursionRules.TaleId(now, IncursionRules.LowSecurityLane), IncursionSecurity.Low);
        long next =
            now >= ends ? IncursionRules.OpensAt(taleIdNow + 1)
            : now < lowFinalAge ? lowFinalAge
            : ends;
        Volatile.Write(ref _nextReview, next);
    }

    internal static SavedIncursionContribution Contribution(WorldAction world, WorldCombatAnomaly site, long pilotId) =>
        world.Set<SavedIncursionContribution>().Find(site.Id, pilotId)
        ?? world
            .Add(
                new SavedIncursionContribution
                {
                    TaleId = site.IncursionTaleId!.Value,
                    SiteId = site.Id,
                    PilotId = pilotId,
                }
            )
            .Entity;

    internal static void RecordKill(WorldAction world, WorldCombatAnomaly site, long npcId, long now)
    {
        if (site.IncursionTaleId is not long taleId || now >= IncursionRules.EndsAt(taleId))
        {
            return;
        }

        foreach (var hit in PilotInteractionHistoryStore.DamageContributions(world, npcId))
        {
            if (
                hit.CharacterId is not long pilotId
                || hit.Damage <= 0
                || world.Pilots.Find(pilotId)?.AccountId is not (> 0 and not CapsuleerCommunityStore.MinerAccountId)
            )
            {
                continue;
            }

            Contribution(world, site, pilotId).Damage += hit.Damage;
        }
    }

    internal static void CompleteSite(WorldAction world, CatalogDbContext catalog, WorldCombatAnomaly site, long now)
    {
        if (
            site.IncursionTaleId is not long taleId
            || now >= IncursionRules.EndsAt(taleId)
            || world.Set<SavedIncursionCampaign>().Find(taleId) is not { DefeatedAt: null } campaign
            || IncursionSiteCatalog.Kind(site.DungeonId) is not { } kind
            || Rewards(catalog, kind, ForTale(taleId).Security) is not { } rewards
        )
        {
            return;
        }

        var place = ForTale(taleId);
        if (kind == IncursionSiteKind.Mothership && !FinalAvailable(campaign, place.Security, now))
        {
            return;
        }

        var fleets = LiveWorld.Find(world.WorldPath)?.Fleets;
        var contributions = world.Filter<SavedIncursionContribution>(row => row.SiteId == site.Id).ToArray();
        var winners =
            contributions
                .Where(rewards.Qualifies)
                .Select(row => (Row: row, Pilot: WorldPilotOnSite(world, catalog, site, row.PilotId, now)))
                .Where(row => row.Pilot is not null)
                .GroupBy(row =>
                    fleets?.Identity(row.Row.PilotId) is { } fleet ? (Fleet: true, Id: fleet.FleetId) : (Fleet: false, Id: row.Row.PilotId)
                )
                .Select(group => (Group: group, Damage: group.Sum(row => row.Row.Damage)))
                .Where(entry => entry.Damage > 0)
                .OrderByDescending(entry => entry.Damage)
                .ThenBy(entry => entry.Group.Key.Id)
                .ThenByDescending(entry => entry.Group.Key.Fleet)
                .Select(entry => entry.Group)
                .FirstOrDefault()
                ?.ToArray()
            ?? [];

        long isk = checked(MarketDefinitions.CentsPerIsk * Amount(winners.Length, rewards.Isk));
        long loyalty = Amount(winners.Length, rewards.Loyalty);
        string dungeonName = loyalty > 0 ? IncursionSiteCatalog.Read(catalog, site.DungeonId)?.Encounter.Name ?? "" : "";
        foreach (var winner in winners)
        {
            if (isk > 0)
            {
                PayIsk(world, winner.Pilot!, site.SystemId, isk, now);
            }
            winner.Row.PendingLoyalty = loyalty;
            if (loyalty > 0)
            {
                world.Add(
                    new SavedIncursionLoyaltyEntry
                    {
                        PilotId = winner.Row.PilotId,
                        SourceId = site.Id,
                        TaleId = taleId,
                        SystemId = site.SystemId,
                        RewardId = (int)rewards.Group,
                        RewardMessageId = rewards.LogNameId,
                        DungeonName = dungeonName,
                        Pilots = winners.Length,
                        Event = IncursionLoyaltyEvent.Stored,
                        Points = loyalty,
                        Time = now,
                    }
                );
            }
        }

        world.RemoveRange(contributions.Where(row => row.PendingLoyalty == 0).ToArray());
        world.RemoveRange(world.Filter<SavedIncursionRepairDamage>(row => row.SiteId == site.Id).ToArray());
        bool wasFinalAvailable = FinalAvailable(campaign, place.Security, now);
        campaign.LastUpdated = Math.Max(campaign.LastUpdated, now);
        if (kind == IncursionSiteKind.Mothership)
        {
            if (
                winners.OrderByDescending(winner => winner.Row.Damage).ThenBy(winner => winner.Row.PilotId).FirstOrDefault().Pilot is
                { } owner
            )
            {
                IncursionLoot.Create(world, catalog, site, owner, place.Security, now);
            }
            campaign.DefeatedAt = now;
            Repel(world, taleId, now);
        }
        else
        {
            campaign.Clears = Math.Min(IncursionRules.ClearsToUnlockFinal, campaign.Clears + 1);
            campaign.HeadquartersCleared |= kind == IncursionSiteKind.Headquarters;
            if (!wasFinalAvailable && FinalAvailable(campaign, place.Security, now))
            {
                CombatAnomalyStore.Invalidate(world, place.Headquarters);
            }
        }
        if (LiveWorld.Find(world.WorldPath) is { } live)
        {
            int lane = IncursionRules.Lane(taleId);
            var focus = ResolveFocus(world, catalog, taleId, now);
            var terminal = campaign.DefeatedAt is null
                ? null
                : new IncursionAgencyUpdate(
                    taleId,
                    campaign.LastUpdated,
                    IncursionRules.Influence(campaign.Clears),
                    false,
                    false,
                    place.Systems,
                    IncursionRules.EndsAt(taleId)
                );
            world.AfterCommit(() => live.SetIncursionFocus(lane, focus, now, terminal));
        }
    }

    private static Pilot? WorldPilotOnSite(WorldAction world, CatalogDbContext catalog, WorldCombatAnomaly site, long pilotId, long now)
    {
        if (
            world.Pilots.Find(pilotId) is not { AccountId: > 0 and not CapsuleerCommunityStore.MinerAccountId } pilot
            || world.Set<SavedLogoff>().Find(pilotId) is not null
            || pilot.ActiveShip.Position is not { } position
            || (position.SystemId ?? catalog.FindStation(pilot.ActiveShip.StationId)?.SolarSystemId) != site.SystemId
            || catalog.FittingType(pilot.ActiveShip.TypeId)?.GroupId is not int group
            || group is (int)HullClass.Capsule or (int)HullClass.Shuttle
            || world.Set<SpatialNavigationState>().Find(pilot.ActiveShip.Id) is { } navigation
                && (navigation.CloakModuleId != 0 || navigation.GateCloakUntil > now)
        )
        {
            return null;
        }

        var current =
            LiveWorld.Find(world.WorldPath)?.CombatForSystem(site.SystemId)?.InformationPosition(pilot.ActiveShip.Id)
            ?? new SpacePosition(position.X, position.Y, position.Z);
        var combatRoom = IncursionSiteCatalog.CombatCenter(site);
        return current.DistanceTo(combatRoom) <= NpcEncounterDefinitions.VisibilityRange ? pilot : null;
    }

    private static void PayIsk(WorldAction world, Pilot pilot, int system, long cents, long now)
    {
        var wallet = world.Set<PilotWallet>().Find(pilot.Id) ?? world.Add(new PilotWallet { PilotId = pilot.Id }).Entity;
        wallet.Cents = checked(wallet.Cents + cents);
        WritePayment(
            world,
            pilot.Id,
            LoyaltyStoreRules.ConcordCorporation,
            system,
            WalletEntryType.CorporateRewardPayout,
            cents,
            wallet.Cents,
            now
        );

        long tax = CorporationWalletRules.Tax(
            cents,
            world.Set<SavedCorporation>().Find(pilot.CorporationId)?.Snapshot(0).Details.TaxRate ?? 0
        );
        if (tax > 0)
        {
            CorporationWalletStore.Pay(world, pilot.CorporationId, pilot.Id, system, WalletEntryType.CorporateRewardTax, tax, now);
            wallet.Cents -= tax;
            WritePayment(world, pilot.Id, pilot.CorporationId, system, WalletEntryType.CorporateRewardTax, -tax, wallet.Cents, now);
        }
    }

    private static void WritePayment(
        WorldAction world,
        long pilotId,
        int counterparty,
        int system,
        WalletEntryType type,
        long amount,
        long balance,
        long now
    ) =>
        world.Add(
            new SavedServicePayment
            {
                PilotId = pilotId,
                CounterpartyId = counterparty,
                ContextId = system,
                Kind = type,
                AmountCents = amount,
                BalanceCents = balance,
                Time = now,
            }
        );

    private static void Repel(WorldAction world, long taleId, long now)
    {
        var contributions = world.Filter<SavedIncursionContribution>(row => row.TaleId == taleId).ToArray();
        foreach (var (pilotId, points) in HeldLoyalty(world, contributions))
        {
            int issuer = LoyaltyStoreRules.ConcordCorporation;
            var wallet =
                world.Set<SavedLoyaltyBalance>().Find(pilotId, issuer)
                ?? world.Add(new SavedLoyaltyBalance { PilotId = pilotId, IssuerCorporationId = issuer }).Entity;
            wallet.Points = checked(wallet.Points + points);
            RecordLoyaltyClose(world, pilotId, taleId, ForTale(taleId).Headquarters, IncursionLoyaltyEvent.Paid, points, now);
        }
        world.RemoveRange(contributions);
        world.RemoveRange(world.Filter<SavedIncursionRepairDamage>(row => row.TaleId == taleId).ToArray());
        CombatAnomalyStore.RetireIncursion(world, taleId, now);
        IncursionRoamingPopulation.Retire(world, ForTale(taleId).Systems, taleId, now);
    }

    private static long Amount(int pilots, ImmutableArray<Breakpoint> entries) =>
        entries.LastOrDefault(entry => entry.PilotCount <= pilots)?.Amount ?? 0;

    private static PayoutTable? LoadRewards(CatalogDbContext catalog, RewardGroup rewardGroup, IncursionSecurity security)
    {
        var profile = catalog.Read(CatalogData.Client.Grouprewards, (long)rewardGroup);
        if (
            profile?.RewardingNpcCorporationID != LoyaltyStoreRules.ConcordCorporation
            || profile.MinimumContribution is not >= 0
            || profile.DamageContributionMultiplier is not double damageMultiplier
            || profile.RepairContributionMultiplier is not double repairMultiplier
            || !double.IsFinite(damageMultiplier)
            || !double.IsFinite(repairMultiplier)
            || damageMultiplier <= 0
            || repairMultiplier <= 0
            || profile.Tables is not { } tables
        )
        {
            return null;
        }

        var isk = Table(RewardType.Isk, false);
        var loyalty = Table(RewardType.Loyalty, true);
        return isk.IsEmpty || loyalty.IsEmpty
            ? null
            : new(
                rewardGroup,
                checked((int)(profile.LpLogTypeNameID ?? RewardLogName(rewardGroup))),
                profile.MinimumContribution.Value,
                damageMultiplier,
                repairMultiplier,
                isk,
                loyalty
            );

        ImmutableArray<Breakpoint> Table(RewardType type, bool delayed)
        {
            var entries = tables
                .Where(table =>
                    (table.SecurityCriteria == (int)security || table.SecurityCriteria == AnySecurityRewardTable)
                    && table.RewardType == (int)type
                    && table.Delayed == (delayed ? 1 : 0)
                )
                .OrderByDescending(table => table.SecurityCriteria)
                .FirstOrDefault()
                ?.Entries;
            if (entries is not { Length: > 0 } list || list.Any(row => row.PlayerCount is null or < 0 || row.Quantity is null or < 0))
            {
                return [];
            }
            return [.. list.OrderBy(row => row.PlayerCount).Select(row => new Breakpoint(row.PlayerCount!.Value, row.Quantity!.Value))];
        }
    }
}
