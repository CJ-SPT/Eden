using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Corporations;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Market;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.World.Combat.Npcs;

internal sealed record CombatDamageSettlement(ShipHealth Health, double Applied, bool Destroyed, NpcBounty? Reward);

internal static class NpcDamagePersistence
{
    internal static CombatDamageSettlement Apply(
        WorldDbContext world,
        CatalogDbContext catalog,
        WorldNpc npc,
        Pilot pilot,
        long attackingShip,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        DamageVector damage,
        long now,
        int? weaponType = null,
        int? sourceType = null,
        bool paidProjectile = false
    )
    {
        if (npc.DestroyedAt.HasValue || npc.RetiredAt.HasValue)
        {
            throw new InvalidDataException("The NPC is no longer available.");
        }
        // A paid missile already checked safety and recorded CONCORD hostility
        // at launch. Its impact cannot recheck a later safety setting or hull.
        if (!paidProjectile || npc.Role != NpcRole.Concord)
        {
            NpcHostility.Record(world, catalog, npc, attackingShip, now);
        }
        CrimewatchPersistence.Npc(world, pilot.Id, npc.SystemId, now);
        var defense = ShipHealth.DefenseFrom(attributes);
        NpcRepairPersistence.Advance(npc, attributes, now);
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
        if (npc.Role == NpcRole.Concord)
        {
            return new(new(npc.Id, defense, health), 0, false, null);
        }
        var result = CombatDamage.Apply(health, defense, damage);
        var attacker = PilotInteractionHistoryStore.Player(world, pilot, attackingShip, weaponType, result.Remaining.Destroyed);
        if (sourceType is > 0)
        {
            attacker = attacker with { ShipTypeId = sourceType };
        }
        PilotInteractionHistoryStore.Damage(world, npc.Id, attacker, result.Applied);
        npc.ShieldCharge = result.Remaining.Shield;
        npc.ArmorDamage = defense.Armor - result.Remaining.Armor;
        npc.StructureDamage = defense.Structure - result.Remaining.Structure;
        npc.UpdatedAt = Math.Max(now, npc.UpdatedAt);
        NpcRepairPersistence.Advance(npc, attributes, now);
        NpcBounty? reward = null;
        var aggression = world.Set<NpcAggression>().Find(npc.Id);
        if (result.Remaining.Destroyed)
        {
            if (aggression is not null)
            {
                world.Remove(aggression);
            }
            if (npc.Role != NpcRole.MissionStructure)
            {
                PilotTaskTrackingStore.Record(world, catalog, pilot.Id, now, TaskEvents.NpcDestroyed);
            }
            npc.DestroyedAt = now;
            NpcPopulationSeeder.RecordKill(world, npc, now);
            Eden.World.Exploration.CombatAnomalyStore.Killed(world, catalog, npc, now);
            Eden.World.Agents.PilotMissionStore.Killed(world, catalog, npc, now);
            PilotSecurityStatusStore.RecordKill(world, catalog, pilot, npc, attributes, now);
            WreckStore.Create(world, catalog, npc, pilot, now);
            var wreck = world.Set<WorldWreck>().Local.SingleOrDefault(w => w.NpcId == npc.Id);
            var loot = world
                .Set<OwnedItem>()
                .Local.Where(i => wreck != null && i.LocationId == wreck.Id)
                .Select(i => new CombatLostItem(i.TypeId, i.FlagId, i.Singleton, i.Quantity, 0, []))
                .ToArray();
            PilotInteractionHistoryStore.Destroyed(
                world,
                pilot.Id,
                $"npc:{npc.Id}:{now}",
                now,
                npc.SystemId,
                PilotInteractionHistoryStore.Npc(npc),
                attacker,
                loot
            );
            PilotInteractionHistoryStore.FactionLoss(world, catalog, pilot, npc, attributes, now);
            double bounty = NpcPopulationRules.AwardsBounty(npc.Role) ? attributes.GetValueOrDefault(DogmaAttributeId.EntityKillBounty) : 0;
            if (!double.IsFinite(bounty) || bounty < 0)
            {
                throw new InvalidDataException("Invalid NPC bounty.");
            }
            long originalCents = checked((long)Math.Round(bounty * MarketDefinitions.CentsPerIsk, MidpointRounding.ToEven));
            long cents = WorldTuningRules.Reward(originalCents, world.Settings.Rewards.BountyMultiplier);
            long skillPoints = NpcSkillPointRewards.FromBounty(originalCents, world.Settings.Progression.KillSkillPointMultiplier);
            if (skillPoints > 0)
            {
                PilotTrainingStore.CreditSilentFreePoints(world, pilot.Id, skillPoints);
            }
            if (cents > 0)
            {
                var wallet = world.Set<PilotWallet>().Find(pilot.Id) ?? world.Set<PilotWallet>().Add(new() { PilotId = pilot.Id }).Entity;
                wallet.Cents = checked(wallet.Cents + cents);
                long tax = CorporationWalletStore.TaxBounty(world, pilot, cents, now);
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
                        npc.Id,
                        ""
                    );
                }
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
                            TaxCents = tax,
                        }
                    )
                    .Entity;
            }
        }
        return new(new(npc.Id, defense, result.Remaining), result.Applied, result.Remaining.Destroyed, reward);
    }

    internal static CombatReward Read(NpcBounty bounty) =>
        new(
            bounty.Id,
            bounty.PilotId,
            bounty.NpcId,
            bounty.TypeId,
            bounty.SystemId,
            bounty.Time,
            bounty.AmountCents,
            bounty.BalanceCents,
            bounty.TaxCents
        );
}
