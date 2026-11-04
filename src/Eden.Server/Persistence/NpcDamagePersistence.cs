using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.Server.Catalog;

namespace Eden.Server.Persistence;

internal sealed record NpcDamageSettlement(ShipHealth Health, double Applied, bool Destroyed, NpcBounty? Reward);

internal static class NpcDamagePersistence
{
    internal static NpcDamageSettlement Apply(
        WorldDbContext world,
        CatalogDbContext catalog,
        WorldNpc npc,
        Pilot pilot,
        long attackingShip,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        DamageVector damage,
        long now
    )
    {
        if (npc.DestroyedAt.HasValue || npc.RetiredAt.HasValue)
        {
            throw new InvalidDataException("The NPC is no longer available.");
        }
        NpcHostility.Record(world, catalog, npc, attackingShip, now);
        var defense = ShipHealth.DefenseFrom(attributes);
        HullDamage health = new(
            ChargedResource.Recharged(
                npc.ShieldCharge,
                defense.Shield,
                defense.ShieldRechargeMilliseconds,
                Math.Max(0, (now - npc.UpdatedAt) / (double)TimeSpan.TicksPerMillisecond)
            ),
            Math.Max(0, defense.Armor - npc.ArmorDamage),
            Math.Max(0, defense.Structure - npc.StructureDamage)
        );
        var result = CombatDamage.Apply(health, defense, damage);
        npc.ShieldCharge = result.Remaining.Shield;
        npc.ArmorDamage = defense.Armor - result.Remaining.Armor;
        npc.StructureDamage = defense.Structure - result.Remaining.Structure;
        npc.UpdatedAt = Math.Max(now, npc.UpdatedAt);
        NpcBounty? reward = null;
        var aggression = world.Set<NpcAggression>().Find(npc.Id);
        if (result.Remaining.Destroyed)
        {
            if (aggression is not null)
            {
                world.Remove(aggression);
            }
            PilotTaskTrackingStore.Record(world, catalog, pilot.Id, now, TaskEvents.NpcDestroyed);
            npc.DestroyedAt = now;
            NpcPopulationSeeder.RecordKill(world, npc, now);
            PilotSecurityStatusStore.RecordKill(world, catalog, pilot, npc, attributes, now);
            WreckStore.Create(world, catalog, npc, pilot, now);
            double bounty = NpcPopulationRules.RewardsPirateKill(npc.Role)
                ? attributes.GetValueOrDefault(DogmaAttributeId.EntityKillBounty)
                : 0;
            if (!double.IsFinite(bounty) || bounty < 0)
            {
                throw new InvalidDataException("Invalid NPC bounty.");
            }
            long cents = checked((long)Math.Round(bounty * MarketDefinitions.CentsPerIsk, MidpointRounding.ToEven));
            if (cents > 0)
            {
                var wallet = world.Set<PilotWallet>().Find(pilot.Id) ?? world.Set<PilotWallet>().Add(new() { PilotId = pilot.Id }).Entity;
                wallet.Cents = checked(wallet.Cents + cents);
                reward = world
                    .Set<NpcBounty>()
                    .Add(
                        new()
                        {
                            PilotId = pilot.Id,
                            NpcId = npc.Id,
                            TypeId = npc.TypeId,
                            SystemId = npc.SystemId,
                            Time = now,
                            AmountCents = cents,
                            BalanceCents = wallet.Cents,
                        }
                    )
                    .Entity;
            }
        }
        return new(new(npc.Id, defense, result.Remaining), result.Applied, result.Remaining.Destroyed, reward);
    }

    internal static CombatReward Read(NpcBounty bounty) =>
        new(bounty.Id, bounty.PilotId, bounty.NpcId, bounty.TypeId, bounty.SystemId, bounty.Time, bounty.AmountCents, bounty.BalanceCents);
}
