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
internal sealed class IncursionStore(WorldDatabase database) : IIncursionDirectory, IIncursionMaintenance
{
    private enum RewardType
    {
        Loyalty = 1,
        Isk = 2,
    }

    private const int HighSecurityRewardTable = 1;

    private sealed record Placement(int ConstellationId, int Staging, ImmutableArray<int> Vanguards)
    {
        internal ImmutableArray<IncursionSystem> Systems { get; } =
        [new(Staging, IncursionScene.Staging), .. Vanguards.Select(system => new IncursionSystem(system, IncursionScene.Vanguard))];
    }

    private sealed record PayoutTable(long MinimumContribution, ImmutableArray<long> Isk, ImmutableArray<long> Loyalty);

    private sealed record CatalogRewards(PayoutTable? HighSecurity);

    // EVE University observed layouts, checked against build 3662168. Each member is high security.
    private static readonly Placement[] s_placements =
    [
        new(20000566, 30003881, [30003878, 30003880, 30003879]), // Amdimmah
        new(20000604, 30004134, [30004133, 30004132, 30004135]), // Ahrnot
        new(20000209, 30001429, [30001428, 30001426, 30001427]), // Asalola
    ];

    private static readonly CatalogDefinition<CatalogRewards> s_rewards = new("incursion-vanguard-rewards", LoadRewards);

    private static Placement ForTale(long taleId) => s_placements[(taleId - 1) % s_placements.Length];

    internal static bool MayHaveSite(int systemId, long now)
    {
        long taleId = IncursionRules.TaleId(now);
        return now >= IncursionRules.OpensAt(taleId) && now < IncursionRules.EndsAt(taleId) && ForTale(taleId).Vanguards.Contains(systemId);
    }

    internal static IncursionFocus? ActiveFocus(WorldAction world, CatalogDbContext catalog, long now)
    {
        long taleId = IncursionRules.TaleId(now);
        long opens = IncursionRules.OpensAt(taleId);
        long expires = IncursionRules.EndsAt(taleId);
        if (
            now < opens
            || now >= expires
            || world.Set<SavedIncursionCampaign>().Find(taleId) is { Clears: >= IncursionRules.ClearsToRepel }
        )
        {
            return null;
        }

        var place = ForTale(taleId);
        if (
            catalog.GlobalDefinition(s_rewards).HighSecurity is null
            || IncursionSiteCatalog.Read(catalog) is null
            || place.Systems.Any(system =>
                catalog.FindSystem(system.SystemId)?.ConstellationId != place.ConstellationId
                || catalog.SystemPolitics(system.SystemId).Security is not >= 0.45
            )
        )
        {
            return null;
        }

        var progress = world.Set<SavedIncursionCampaign>().Find(taleId);
        return new(
            taleId,
            place.Staging,
            place.ConstellationId,
            progress?.LastUpdated ?? opens,
            expires,
            IncursionRules.Influence(progress?.Clears ?? 0),
            place.Systems
        );
    }

    private LiveWorld Live =>
        LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Incursions require the resident world.");

    public IReadOnlyList<IncursionFocus> Read(long now)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return [];
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return live.Economy.Inspect(world => ActiveFocus(world, catalog, now) is { } focus ? new[] { focus } : []);
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
            world.Remove(campaign);
            world.Commit();
        }
    }

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

            var contribution =
                world.Set<SavedIncursionContribution>().Find(site.Id, pilotId)
                ?? world
                    .Add(
                        new SavedIncursionContribution
                        {
                            TaleId = taleId,
                            SiteId = site.Id,
                            PilotId = pilotId,
                        }
                    )
                    .Entity;
            contribution.Damage += hit.Damage;
        }
    }

    internal static void CompleteSite(WorldAction world, CatalogDbContext catalog, WorldCombatAnomaly site, long now)
    {
        if (
            site.IncursionTaleId is not long taleId
            || now >= IncursionRules.EndsAt(taleId)
            || world.Set<SavedIncursionCampaign>().Find(taleId) is not { Clears: < IncursionRules.ClearsToRepel } campaign
            || catalog.GlobalDefinition(s_rewards).HighSecurity is not { } rewards
        )
        {
            return;
        }

        var fleets = LiveWorld.Find(world.WorldPath)?.Fleets;
        var contributions = world.Filter<SavedIncursionContribution>(row => row.SiteId == site.Id).ToArray();
        var winners =
            contributions
                .Where(row => row.Damage >= rewards.MinimumContribution)
                .Select(row => (Row: row, Pilot: WorldPilotOnSite(world, catalog, site, row.PilotId, now)))
                .Where(row => row.Pilot is not null)
                .GroupBy(row =>
                    fleets?.Identity(row.Row.PilotId) is { } fleet ? (Fleet: true, Id: fleet.FleetId) : (Fleet: false, Id: row.Row.PilotId)
                )
                .OrderByDescending(group => group.Sum(row => row.Row.Damage))
                .ThenBy(group => group.Key.Id)
                .ThenByDescending(group => group.Key.Fleet)
                .FirstOrDefault()
                ?.ToArray()
            ?? [];

        long isk = checked(MarketDefinitions.CentsPerIsk * IncursionRules.Payout(winners.Length, rewards.Isk));
        long loyalty = IncursionRules.Payout(winners.Length, rewards.Loyalty);
        foreach (var winner in winners)
        {
            if (isk > 0)
            {
                PayIsk(world, winner.Pilot!, site.SystemId, isk, now);
            }
            winner.Row.PendingLoyalty = loyalty;
        }

        world.RemoveRange(contributions.Where(row => row.PendingLoyalty == 0).ToArray());
        campaign.Clears++;
        campaign.LastUpdated = Math.Max(campaign.LastUpdated, now);
        if (campaign.Clears == IncursionRules.ClearsToRepel)
        {
            Repel(world, taleId, now);
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
        var combatRoom = new SpacePosition(site.X, site.Y, site.Z) + IncursionSiteCatalog.CombatOffset;
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
        CombatAnomalyStore.RetireIncursion(world, taleId, now);
    }

    private static CatalogRewards LoadRewards(CatalogDbContext catalog, int unused)
    {
        var profile = catalog.Read(CatalogData.Client.Grouprewards, (long)IncursionRules.VanguardRewardGroup);
        if (
            profile?.RewardingNpcCorporationID != LoyaltyStoreRules.ConcordCorporation
            || profile.MinimumContribution is not > 0
            || profile.Tables is not { } tables
        )
        {
            return new(HighSecurity: null);
        }

        var isk = Table(RewardType.Isk, 0);
        var loyalty = Table(RewardType.Loyalty, 1);
        return new(isk.IsDefaultOrEmpty || loyalty.IsDefaultOrEmpty ? null : new(profile.MinimumContribution.Value, isk, loyalty));

        ImmutableArray<long> Table(RewardType type, int delayed)
        {
            var entries = tables
                .SingleOrDefault(table =>
                    table.SecurityCriteria == HighSecurityRewardTable && table.RewardType == (int)type && table.Delayed == delayed
                )
                ?.Entries;
            if (entries is not { Length: > 0 } list || list.Any(row => row.PlayerCount is null or < 0 || row.Quantity is null or < 0))
            {
                return [];
            }

            var ordered = list.OrderBy(row => row.PlayerCount).ToArray();
            return
            [
                .. Enumerable
                    .Range(0, IncursionRules.MaximumPaidFleetSize + 1)
                    .Select(count => ordered.LastOrDefault(row => row.PlayerCount <= count)?.Quantity ?? 0),
            ];
        }
    }
}
