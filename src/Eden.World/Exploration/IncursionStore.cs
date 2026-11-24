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
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Exploration;

[Injectable(InjectionType.Singleton)]
internal sealed partial class IncursionStore(WorldDatabase database) : IIncursionDirectory, IIncursionMaintenance
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
    private const int HighSecurityRewardTable = 1;

    private sealed record Placement(int ConstellationId, int Staging, ImmutableArray<int> Vanguards, int Assault, int Headquarters)
    {
        internal ImmutableArray<IncursionSystem> Systems { get; } =
        [
            new(Staging, IncursionScene.Staging),
            .. Vanguards.Select(system => new IncursionSystem(system, IncursionScene.Vanguard)),
            new(Assault, IncursionScene.Assault),
            new(Headquarters, IncursionScene.Headquarters),
        ];

        internal bool HasSystem(int system) =>
            Staging == system || Assault == system || Headquarters == system || Vanguards.Contains(system);
    }

    private sealed record Breakpoint(long PilotCount, long Amount);

    private sealed record PayoutTable(
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

    // EVE University observed layouts, checked against build 3662168. Each member is high security.
    private static readonly Placement[] s_placements =
    [
        new(20000566, 30003881, [30003878, 30003880, 30003879], 30003877, 30003876), // Amdimmah
        new(20000604, 30004134, [30004133, 30004132, 30004135], 30004131, 30004130), // Ahrnot
        new(20000209, 30001429, [30001428, 30001426, 30001427], 30001425, 30001424), // Asalola
    ];

    private static readonly CatalogDefinition<ImmutableDictionary<RewardGroup, PayoutTable>> s_rewards = new(
        "incursion-site-rewards",
        (catalog, _) =>
            Enum.GetValues<RewardGroup>()
                .Select(group => (Group: group, Reward: LoadRewards(catalog, group)))
                .Where(entry => entry.Reward is not null)
                .ToImmutableDictionary(entry => entry.Group, entry => entry.Reward!)
    );
    private static readonly IncursionSiteKind[] s_siteKinds = Enum.GetValues<IncursionSiteKind>();

    private static PayoutTable? Rewards(CatalogDbContext catalog, IncursionSiteKind kind) =>
        catalog
            .GlobalDefinition(s_rewards)
            .GetValueOrDefault(
                kind switch
                {
                    IncursionSiteKind.Scout => RewardGroup.Scout,
                    IncursionSiteKind.Vanguard => RewardGroup.Vanguard,
                    IncursionSiteKind.Assault => RewardGroup.Assault,
                    IncursionSiteKind.Headquarters => RewardGroup.Headquarters,
                    IncursionSiteKind.Mothership => RewardGroup.Mothership,
                    _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                }
            );

    internal static void PrepareCatalog(CatalogDbContext catalog)
    {
        _ = IncursionSiteCatalog.Read(catalog);
        _ = catalog.GlobalDefinition(s_rewards);
    }

    private static Placement ForTale(long taleId) => s_placements[(taleId - 1) % s_placements.Length];

    internal static bool MayHaveSite(int systemId, long now)
    {
        long taleId = IncursionRules.TaleId(now);
        return now >= IncursionRules.OpensAt(taleId) && now < IncursionRules.EndsAt(taleId) && ForTale(taleId).HasSystem(systemId);
    }

    internal static IncursionFocus? ActiveFocus(WorldAction world, CatalogDbContext catalog, long now)
    {
        long taleId = IncursionRules.TaleId(now);
        long opens = IncursionRules.OpensAt(taleId);
        long expires = IncursionRules.EndsAt(taleId);
        var progress = world.Set<SavedIncursionCampaign>().Find(taleId);
        if (now < opens || now >= expires || progress?.DefeatedAt is not null)
        {
            return null;
        }

        var place = ForTale(taleId);
        if (
            s_siteKinds.Any(kind => IncursionSiteCatalog.Read(catalog, kind) is null || Rewards(catalog, kind) is null)
            || place.Systems.Any(system =>
                catalog.FindSystem(system.SystemId)?.ConstellationId != place.ConstellationId
                || catalog.SystemPolitics(system.SystemId).Security is not >= 0.45
            )
        )
        {
            return null;
        }

        return new(
            taleId,
            place.Staging,
            place.ConstellationId,
            progress?.LastUpdated ?? opens,
            expires,
            IncursionRules.Influence(progress?.Clears ?? 0),
            place.Systems,
            FinalAvailable(progress)
        );
    }

    private static bool FinalAvailable(SavedIncursionCampaign? campaign) =>
        campaign is { DefeatedAt: null } && IncursionRules.FinalAvailable(campaign.Clears, campaign.HeadquartersCleared);

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
            var focus = ActiveFocus(world, catalog, now);
            PublishAgency(live, focus, now);
            return focus is not null ? new[] { focus } : [];
        });
    }

    public IncursionAgencyUpdate? AgencyUpdate => LiveWorld.Find(database.Path)?.IncursionAgency;

    private static void PublishAgency(LiveWorld live, IncursionFocus? focus, long now)
    {
        if (focus is not null)
        {
            live.SetIncursionAgency(new(focus.TaleId, focus.LastUpdated, focus.Influence, focus.HasFinalEncounter, IsActive: true));
        }
        else if (live.IncursionAgency is { IsActive: true } previous)
        {
            live.SetIncursionAgency(
                previous with
                {
                    IsActive = false,
                    HasFinalEncounter = false,
                    LastUpdated = Math.Min(now, IncursionRules.EndsAt(previous.TaleId)),
                }
            );
        }
    }

    public IncursionPendingLoyalty? PendingLoyalty(long pilotId, long now)
    {
        long taleId = IncursionRules.TaleId(now);
        if (now >= IncursionRules.EndsAt(taleId) || LiveWorld.Find(database.Path) is not { } live)
        {
            return null;
        }

        return live.Economy.Inspect<IncursionPendingLoyalty?>(world =>
        {
            long points = world
                .Filter<SavedIncursionContribution>(row => row.TaleId == taleId)
                .Where(row => row.PilotId == pilotId)
                .Sum(row => row.PendingLoyalty);
            return points > 0 ? new(taleId, points) : null;
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
            world.RemoveRange(world.Filter<SavedIncursionContribution>(row => row.TaleId == taleId).ToArray());
            world.RemoveRange(world.Filter<SavedIncursionRepairDamage>(row => row.TaleId == taleId).ToArray());
            world.Remove(campaign);
            world.Commit();
        }
        _ = Read(now);
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
            || (kind == IncursionSiteKind.Mothership && !FinalAvailable(campaign))
            || Rewards(catalog, kind) is not { } rewards
        )
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
        foreach (var winner in winners)
        {
            if (isk > 0)
            {
                PayIsk(world, winner.Pilot!, site.SystemId, isk, now);
            }
            winner.Row.PendingLoyalty = loyalty;
        }

        world.RemoveRange(contributions.Where(row => row.PendingLoyalty == 0).ToArray());
        world.RemoveRange(world.Filter<SavedIncursionRepairDamage>(row => row.SiteId == site.Id).ToArray());
        bool wasFinalAvailable = FinalAvailable(campaign);
        campaign.LastUpdated = Math.Max(campaign.LastUpdated, now);
        if (kind == IncursionSiteKind.Mothership)
        {
            campaign.DefeatedAt = now;
            Repel(world, taleId, now);
        }
        else
        {
            campaign.Clears = Math.Min(IncursionRules.ClearsToUnlockFinal, campaign.Clears + 1);
            campaign.HeadquartersCleared |= kind == IncursionSiteKind.Headquarters;
            if (!wasFinalAvailable && FinalAvailable(campaign))
            {
                CombatAnomalyStore.Invalidate(world, ForTale(taleId).Headquarters);
            }
        }
        if (LiveWorld.Find(world.WorldPath) is { } live)
        {
            IncursionAgencyUpdate update = new(
                taleId,
                campaign.LastUpdated,
                IncursionRules.Influence(campaign.Clears),
                FinalAvailable(campaign),
                campaign.DefeatedAt is null
            );
            world.AfterCommit(() => live.SetIncursionAgency(update));
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
        foreach (var award in contributions.Where(row => row.PendingLoyalty > 0).GroupBy(row => row.PilotId))
        {
            if (!world.PilotExists(award.Key) || world.Set<SavedCharacterDeletion>().Find(award.Key)?.DeletedAt is not null)
            {
                continue;
            }

            int issuer = LoyaltyStoreRules.ConcordCorporation;
            var wallet =
                world.Set<SavedLoyaltyBalance>().Find(award.Key, issuer)
                ?? world.Add(new SavedLoyaltyBalance { PilotId = award.Key, IssuerCorporationId = issuer }).Entity;
            wallet.Points = checked(wallet.Points + award.Sum(row => row.PendingLoyalty));
        }
        world.RemoveRange(contributions);
        world.RemoveRange(world.Filter<SavedIncursionRepairDamage>(row => row.TaleId == taleId).ToArray());
        CombatAnomalyStore.RetireIncursion(world, taleId, now);
    }

    private static long Amount(int pilots, ImmutableArray<Breakpoint> entries) =>
        entries.LastOrDefault(entry => entry.PilotCount <= pilots)?.Amount ?? 0;

    private static PayoutTable? LoadRewards(CatalogDbContext catalog, RewardGroup rewardGroup)
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
            : new(profile.MinimumContribution.Value, damageMultiplier, repairMultiplier, isk, loyalty);

        ImmutableArray<Breakpoint> Table(RewardType type, bool delayed)
        {
            var entries = tables
                .Where(table =>
                    table.SecurityCriteria is HighSecurityRewardTable or AnySecurityRewardTable
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
