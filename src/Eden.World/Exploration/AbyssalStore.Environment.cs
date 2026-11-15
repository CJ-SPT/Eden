using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;

namespace Eden.World.Exploration;

internal sealed partial class AbyssalStore
{
    private void Environment(WorldAction world, CatalogDbContext catalog, SavedAbyssalRun run, long now)
    {
        if (now < run.EnvironmentAt + TimeSpan.TicksPerSecond || now >= run.ExpiresAt)
        {
            return;
        }
        long previous = run.EnvironmentAt;
        run.EnvironmentAt = now;
        foreach (var participant in world.Filter<SavedAbyssalParticipant>(p => p.RunId == run.Id && p.Entered && !p.Finished).ToArray())
        {
            var pilot = world.Pilots.Find(participant.PilotId)!;
            if (pilot.ActiveShipId != participant.ShipId || pilot.ActiveShip.Position is not { } position)
            {
                continue;
            }
            double distance = new SpacePosition(position.X, position.Y, position.Z).DistanceTo(AbyssalRules.Center(participant.Room));
            double amount = AbyssalEnvironmentRules.BoundaryDamage(distance);
            if (amount == 0)
            {
                participant.OutsideSince = 0;
                continue;
            }
            if (participant.OutsideSince == 0)
            {
                participant.OutsideSince = now;
            }
            if (now - participant.OutsideSince >= AbyssalEnvironmentRules.TerminalExposure)
            {
                Fail(world, catalog, run, participant, pilot, now);
                continue;
            }
            var defense = ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, pilot));
            var damage = new DamageVector(amount, 0, 0, 0);
            var result = PilotHealthStore.Apply(world, pilot.ActiveShipId, defense, damage, now, bypassResistances: true);
            if (result.Remaining.Destroyed)
            {
                Fail(world, catalog, run, participant, pilot, now);
            }
            else
            {
                var notification = new PilotDamage(
                    pilot.Id,
                    0,
                    AbyssalEnvironmentRules.FieldId(run.Id, participant.Room, 3),
                    new(pilot.ActiveShipId, defense, result.Remaining),
                    damage,
                    result.Applied,
                    1,
                    AbyssalEnvironmentRules.BoundaryType,
                    false,
                    null,
                    null
                );
                world.AfterCommit(() => damageEvents.Publish(notification));
            }
        }
        // Cross an absolute pulse bucket once. Restart and repeated callbacks cannot replay it.
        if (previous / AbyssalEnvironmentRules.PulseInterval == now / AbyssalEnvironmentRules.PulseInterval)
        {
            return;
        }
        foreach (
            var field in Enumerable
                .Range(0, run.Room + 1)
                .SelectMany(room => AbyssalEnvironmentRules.Layout(room, run.Seed))
                .Where(f => f.TypeId is AbyssalEnvironmentRules.ShortSuppressor or AbyssalEnvironmentRules.MediumSuppressor)
        )
        {
            Suppress(world, catalog, run, field, now);
        }
    }

    private static void Suppress(WorldAction world, CatalogDbContext catalog, SavedAbyssalRun run, AbyssalField field, long now)
    {
        if (
            !world
                .Filter<WorldNpc>(n =>
                    n.EncounterId == run.Id
                    && n.AnchorKind == NpcAnchorKind.Abyssal
                    && n.TypeId == field.TypeId
                    && n.SpawnSlot / AbyssalRules.ObjectsPerRun == AbyssalContent.RoomAt(field.Position)
                    && n.DestroyedAt == null
                    && n.RetiredAt == null
                )
                .Any()
        )
        {
            return;
        }
        var damage = AbyssalEnvironmentRules.SuppressorDamage(field.TypeId);
        bool Near(SpacePosition position) => position.DistanceTo(field.Position) <= field.Range;
        foreach (
            var npc in world.Filter<WorldNpc>(n => n.SystemId == run.SystemId && n.DestroyedAt == null && n.RetiredAt == null).ToArray()
        )
        {
            var type = catalog.RequireItem(npc.TypeId);
            if (!AbyssalEnvironmentRules.SuppressorTarget(type.Id, type.GroupId, type.CategoryId) || !Near(new(npc.X, npc.Y, npc.Z)))
            {
                continue;
            }
            var attributes = AbyssalEnvironmentStore.NpcAttributes(world, npc.Id, catalog.FittingAttributes(npc.TypeId));
            var defense = ShipHealth.DefenseFrom(attributes);
            var health = new HullDamage(
                ChargedResource.Recharged(
                    npc.ShieldCharge,
                    defense.Shield,
                    defense.ShieldRechargeMilliseconds,
                    Math.Max(0, now - npc.UpdatedAt) / (double)TimeSpan.TicksPerMillisecond
                ),
                Math.Max(0, defense.Armor - npc.ArmorDamage),
                Math.Max(0, defense.Structure - npc.StructureDamage)
            );
            var result = CombatDamage.Apply(health, defense, damage);
            npc.ShieldCharge = result.Remaining.Shield;
            npc.ArmorDamage = defense.Armor - result.Remaining.Armor;
            npc.StructureDamage = defense.Structure - result.Remaining.Structure;
            npc.UpdatedAt = now;
            if (result.Remaining.Destroyed)
            {
                npc.DestroyedAt = now;
                if (world.Set<NpcAggression>().Find(npc.Id) is { } aggression)
                {
                    world.Remove(aggression);
                }
            }
        }
        foreach (var drone in world.Filter<WorldDrone>(d => d.SystemId == run.SystemId).ToArray())
        {
            if (Near(new(drone.X, drone.Y, drone.Z)))
            {
                SmartbombStore.DamageDrone(world, drone, PilotDroneStore.Contact(world, catalog, drone), damage);
            }
        }
        foreach (
            var missile in world.Filter<WorldMissile>(m => m.SystemId == run.SystemId && m.Status == MissileFlightStatus.Flying).ToArray()
        )
        {
            var payload = missile.Payload();
            if (Near(payload.Flight.Position))
            {
                payload = payload with { EnvironmentDamage = payload.EnvironmentDamage + damage.Total };
                if (
                    payload.EnvironmentDamage
                    >= catalog.FittingAttributes(missile.ChargeTypeId).GetValueOrDefault(DogmaAttributeId.StructureHitPoints)
                )
                {
                    missile.Status = MissileFlightStatus.Expired;
                    missile.EndedAt = now;
                    payload = payload with { Flight = payload.Flight with { Status = MissileFlightStatus.Expired } };
                }
                missile.Save(payload);
            }
        }
    }
}
