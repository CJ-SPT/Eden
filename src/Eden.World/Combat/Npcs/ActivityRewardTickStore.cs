using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Market;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.World.Combat.Npcs;

[Injectable(InjectionType.Singleton)]
internal sealed class ActivityRewardTickStore(WorldDatabase database, TimeProvider clock) : IActivityRewardTicks
{
    public void Advance()
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Activity rewards require the resident world.");
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        List<Exception>? failures = null;
        foreach (var (dueAt, pilotId) in live.Economy.DueActivityRewards(now))
        {
            try
            {
                using var world = live.Economy.Begin(new(pilots: [pilotId]));
                if (world.Set<SavedActivityRewardTick>().Find(pilotId, dueAt) is not { } tick)
                {
                    continue;
                }
                if (world.Pilots.Find(pilotId) is { } pilot)
                {
                    CreditSkillPoints(world, pilotId, tick.SkillPoints);
                    foreach (var (system, bounty) in tick.Bounties)
                    {
                        PayBounty(world, pilot, system, bounty, now);
                    }
                }
                world.Remove(tick);
                world.Commit();
            }
            catch (Exception error)
            {
                (failures ??= []).Add(new InvalidOperationException($"Activity reward tick failed for pilot {pilotId}.", error));
            }
        }
        if (failures is not null)
        {
            throw new AggregateException(failures);
        }
    }

    internal static void RecordMining(WorldAction world, long pilotId, long points, long now)
    {
        if (points > 0 && !TryAccrue(world, pilotId, points, now))
        {
            CreditSkillPoints(world, pilotId, points);
        }
    }

    internal static NpcBounty? RecordBounty(WorldAction world, Pilot pilot, WorldNpc npc, long cents, long points, long now)
    {
        if (TryAccrue(world, pilot.Id, points, now, npc, cents))
        {
            return null;
        }
        CreditSkillPoints(world, pilot.Id, points);
        return PayBounty(world, pilot, npc.SystemId, new(npc.Id, npc.TypeId, cents), now);
    }

    private static bool TryAccrue(WorldAction world, long pilotId, long points, long now, WorldNpc? npc = null, long cents = 0)
    {
        int interval = world.Settings.Rewards.BountyTickSeconds;
        if (interval == 0 || world is not EconomyTransaction)
        {
            return false;
        }

        // A late maintenance pass cannot pull an award at or after the deadline into an older tick.
        var tick =
            world
                .Filter<SavedActivityRewardTick>(row => row.PilotId == pilotId && row.DueAt > now)
                .OrderBy(row => row.DueAt)
                .FirstOrDefault()
            ?? world.Add(new SavedActivityRewardTick { PilotId = pilotId, DueAt = ActivityRewardTickRules.Deadline(now, interval) }).Entity;
        if (points > 0)
        {
            long pending = FreeSkillPointRules.Credit(tick.SkillPoints, points);
            _ = FreeSkillPointRules.Credit(PilotTrainingStore.ReadFreePoints(world, pilotId), pending);
            tick.SkillPoints = pending;
        }
        if (npc is not null)
        {
            var bounty = tick.Bounties.GetValueOrDefault(npc.SystemId);
            tick.Bounties = tick.Bounties.SetItem(
                npc.SystemId,
                bounty is null ? new(npc.Id, npc.TypeId, cents) : bounty with { AmountCents = checked(bounty.AmountCents + cents) }
            );
        }
        return true;
    }

    private static void CreditSkillPoints(WorldAction world, long pilotId, long points)
    {
        if (points > 0)
        {
            PilotTrainingStore.CreditSilentFreePoints(world, pilotId, points);
        }
    }

    private static NpcBounty PayBounty(WorldAction world, Pilot pilot, int system, SavedActivityBounty bounty, long now)
    {
        var wallet = world.Set<PilotWallet>().Find(pilot.Id) ?? world.Set<PilotWallet>().Add(new() { PilotId = pilot.Id }).Entity;
        wallet.Cents = checked(wallet.Cents + bounty.AmountCents);
        long tax = CorporationWalletStore.TaxBounty(world, pilot, bounty.AmountCents, now);
        wallet.Cents -= tax;
        if (tax > 0)
        {
            CorporationWalletStore.AddEntry(
                world,
                pilot.Id,
                CorporationWalletRules.MasterKey,
                now,
                pilot.CorporationId,
                -tax,
                wallet.Cents,
                CorporationWalletRules.BountyTaxEntry,
                bounty.FirstNpcId,
                ""
            );
        }
        return world
            .Add(
                new NpcBounty
                {
                    PilotId = pilot.Id,
                    NpcId = bounty.FirstNpcId,
                    TypeId = bounty.FirstNpcTypeId,
                    SystemId = system,
                    Time = now,
                    AmountCents = bounty.AmountCents,
                    BalanceCents = wallet.Cents,
                    TaxCents = tax,
                }
            )
            .Entity;
    }
}
