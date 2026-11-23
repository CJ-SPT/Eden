using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.FactionWarfare;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Combat.Npcs;

internal static partial class NpcDamagePersistence
{
    internal static CombatDamageSettlement Apply(
        WorldAction world,
        CatalogDbContext catalog,
        WorldNpc npc,
        Pilot pilot,
        long attackingShip,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        DamageVector damage,
        long now,
        int? weaponType = null,
        int? sourceType = null,
        bool paidProjectile = false,
        bool bypassResistances = false,
        Eden.Gameplay.Characters.CombatParty? sourceParty = null
    )
    {
        if (npc.DestroyedAt.HasValue || npc.RetiredAt.HasValue)
        {
            throw new InvalidDataException("The NPC is no longer available.");
        }
        if (npc.Role == NpcRole.MilitiaHub)
        {
            return MilitiaHubStore.Apply(world, npc, pilot, attributes, damage, now, bypassResistances);
        }
        if (!MilitiaObjectiveStore.CanAttack(world, npc, pilot))
        {
            throw new InvalidDataException("Only the opposing militia may attack this objective.");
        }
        // A paid missile already checked safety and recorded CONCORD hostility
        // at launch. Its impact cannot recheck a later safety setting or hull.
        if ((!paidProjectile || npc.Role != NpcRole.Concord) && world.Structure(attackingShip) is null)
        {
            NpcHostility.Record(world, catalog, npc, attackingShip, now);
        }
        if (npc.Role != NpcRole.Sentry)
        {
            CrimewatchPersistence.Npc(world, pilot.Id, npc.SystemId, now);
        }
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
        if (npc.Role is NpcRole.Concord or NpcRole.Sentry)
        {
            return new(new(npc.Id, defense, health), 0, false, null);
        }
        var result = CombatDamage.Apply(
            health,
            bypassResistances ? CombatDamage.WithoutResistances(defense) : defense,
            damage,
            npc.Id,
            now
        );
        if (npc.Role == NpcRole.MissionStructure && npc.AnchorKind == NpcAnchorKind.Mission && result.Applied > 0)
        {
            Eden.World.Agents.PilotMissionStore.CareerStructureDamaged(world, catalog, npc, now);
        }
        var attacker =
            sourceParty ?? PilotInteractionHistoryStore.Player(world, pilot, attackingShip, weaponType, result.Remaining.Destroyed);
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
            npc.DestroyedAt = now;
            if (
                npc.AnchorKind == NpcAnchorKind.Mission
                && world.Set<SavedMission>().Find(npc.EncounterId - MissionRules.FirstSiteId) is { } mission
                && EpicArcCatalog.IsSimulation(mission.MissionId)
            )
            {
                PilotMissionStore.Killed(world, catalog, npc, now);
                return new(new(npc.Id, defense, result.Remaining), result.Applied, true, null);
            }
            if (npc.Role != NpcRole.MissionStructure)
            {
                PilotTaskTrackingStore.Record(world, catalog, pilot.Id, now, TaskEvents.NpcDestroyed);
            }
            MilitiaObjectiveStore.Destroyed(world, catalog, npc, pilot, now);
            if (npc.AnchorKind == NpcAnchorKind.Abyssal)
            {
                Eden.World.Exploration.AbyssalStore.Killed(world, catalog, npc, pilot, now);
                return new(new(npc.Id, defense, result.Remaining), result.Applied, true, null);
            }
            if (
                npc.Role is NpcRole.Pirate or NpcRole.Navy or NpcRole.Customs or NpcRole.Concord or NpcRole.MilitiaNavy
                && !NpcMotionRules.IsStationary(catalog.RequireItem(npc.TypeId).GroupId, npc.Role)
            )
            {
                Maps.MapStatisticsStore.Record(world, npc.SystemId, now, Eden.Gameplay.Maps.MapActivityKind.NpcShipLoss);
                if (npc.Role == NpcRole.Pirate)
                {
                    Eden.World.Sovereignty.SovereigntyDevelopmentPersistence.Record(
                        world,
                        npc.SystemId,
                        Eden.Gameplay.Sovereignty.SovereigntyActivityKind.Military,
                        1,
                        now
                    );
                }
            }
            NpcPopulationSeeder.RecordKill(world, npc, now);
            Eden.World.Exploration.CombatAnomalyStore.Killed(world, catalog, npc, now, pilot.Id);
            Eden.World.Agents.PilotMissionStore.Killed(world, catalog, npc, now);
            PilotSecurityStatusStore.RecordKill(world, catalog, pilot, npc, attributes, now);
            if (npc.Role != NpcRole.MilitiaObjective)
            {
                WreckStore.Create(world, catalog, npc, pilot, now);
            }
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
            bool militiaMission = Eden.World.Agents.MilitiaMissionCatalog.IsMilitia(world, npc);
            if (!militiaMission && npc.AnchorKind is not (NpcAnchorKind.MilitiaComplex or NpcAnchorKind.MilitiaObjective))
            {
                PilotInteractionHistoryStore.FactionLoss(world, catalog, pilot, npc, attributes, now);
            }
            long originalCents = militiaMission ? 0 : NpcBountyRules.BaseCents(npc.Role, attributes);
            long cents = WorldTuningRules.Reward(originalCents, world.Settings.Rewards.BountyMultiplier);
            long skillPoints = NpcSkillPointRewards.FromBounty(
                cents,
                world.Settings.Progression.KillSkillPointMultiplier,
                world.Settings.Progression.KillSkillPointPercent
            );
            if (cents > 0)
            {
                var live = LiveWorld.Find(world.WorldPath);
                var position = new SpacePosition(npc.X, npc.Y, npc.Z);
                var others =
                    live?.Fleets?.Fleetmates(pilot.Id)
                        .Where(member => EligibleForBounty(member, npc.SystemId, position, world))
                        .Select(member => member.Id)
                        .ToArray()
                    ?? [];
                long share = cents / (others.Length + 1);
                reward = ActivityRewardTickStore.RecordBounty(world, pilot, npc, cents - share * others.Length, skillPoints, now);
                if (share > 0)
                {
                    foreach (long recipient in others)
                    {
                        ActivityRewardTickStore.RecordBounty(world, world.Pilots.Find(recipient)!, npc, share, 0, now);
                    }
                }
            }
        }
        return new(new(npc.Id, defense, result.Remaining), result.Applied, result.Remaining.Destroyed, reward);
    }

    private static bool EligibleForBounty(FleetPilot member, int system, SpacePosition kill, WorldAction world)
    {
        if (member.SystemId != system || member.InTransit || member.Position is null || !world.PilotExists(member.Id))
        {
            return false;
        }
        var position = LiveWorld.Find(world.WorldPath)?.CombatForSystem(system)?.InformationPosition(member.ShipId) ?? member.Position;
        return position.DistanceTo(kill) <= FleetRules.CommandRange;
    }
}
