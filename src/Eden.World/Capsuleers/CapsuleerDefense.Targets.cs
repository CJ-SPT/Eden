using System.Collections.Frozen;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;

namespace Eden.World.Capsuleers;

internal sealed record CapsuleerWeaponObservation(ObservedPvpWeapon Kind, bool UsesCapacitor);

internal sealed partial class CapsuleerDefense
{
    private static readonly CatalogDefinition<CapsuleerWeaponObservation> s_observedWeapon = new(
        "capsuleer-observed-weapon",
        (catalog, type) =>
            new(
                catalog.RequireItem(type).CategoryId == (int)InventoryCategory.Drone
                    ? ObservedPvpWeapon.Drone
                    : FittingCatalog.DefaultEffect(catalog, type) switch
                    {
                        DogmaEffect.UseMissiles or DogmaEffect.AutoTargetingMissile => ObservedPvpWeapon.Missile,
                        DogmaEffect.LaserFired or DogmaEffect.ProjectileFired => ObservedPvpWeapon.Turret,
                        _ => ObservedPvpWeapon.Unknown,
                    },
                catalog.FittingAttributes(type).GetValueOrDefault(DogmaAttributeId.CapacitorNeed) > 0
            )
    );

    private static PvpSupportModule SupportModule(CapsuleerDefenseEquipment module) =>
        new(
            module.Effect,
            module.Attributes.GetValueOrDefault(DogmaAttributeId.MaxRange),
            module.Attributes.GetValueOrDefault(DogmaAttributeId.FalloffEffectiveness)
        );

    private CapsuleerWeaponObservation WeaponObserved(LiveCombatShip? ship, long source) =>
        ship?.Contributions.GetValueOrDefault(source)?.WeaponTypeId is { } type
            ? database.Catalog.Definition(s_observedWeapon, type)
            : new(ObservedPvpWeapon.Unknown, false);

    private IReadOnlyDictionary<long, CapsuleerAttacker?> SupportTargets(
        DockedPilot pilot,
        CapsuleerDefenseProfile profile,
        CapsuleerDefenseState state,
        CapsuleerAttacker primary,
        LiveCombatShip? ship,
        RoamingGang? gang,
        long now,
        int stamp,
        bool retreating
    )
    {
        if (primary.Npc || retreating || !profile.Modules.Any(module => PvpSupport.Independent(module.Effect)))
        {
            return FrozenDictionary<long, CapsuleerAttacker?>.Empty;
        }
        var live = LiveWorld.Find(database.Path);
        long elapsed = live?.CapsuleerActivity.Read().ElapsedTicks ?? (long)stamp * TimeSpan.TicksPerSecond;
        var playerJams = warfare.Incoming(pilot.ShipId);
        var npcJams = npcWarfare.Incoming(pilot.ShipId);
        bool Allowed(long id, Eden.Gameplay.Flight.SpacePosition position, double radius) =>
            pilot.Position!.DistanceTo(position) - pilot.Flight.ShipRadius - radius <= profile.TargetRange
            && PlayerEwarRules.CanTarget(playerJams, id)
            && NpcEwarRules.CanTarget(npcJams, id);
        int? group =
            database.Settings.Capsuleers.RegionalConflicts && live is not null
                ? RegionalConflictStore.GroupForPilot(live, database.Catalog, pilot.PilotId)
                : null;
        var seen = PatrolRules
            .Candidates(pilot, pilot.Position!, Observe(pilot, elapsed))
            .Where(contact =>
                !PatrolRules.Friendly(pilot, contact, group)
                && gang?.Members.Any(member => member.ShipId == contact.ShipId) != true
                && (gang?.Regional is not { } order || RegionalConflictStore.CanTarget(database, order, contact, elapsed))
            )
            .Select(contact =>
            {
                if (ship?.Contributions.GetValueOrDefault(contact.ShipId)?.WeaponTypeId is not { } weapon)
                {
                    return contact;
                }
                var observed = database.Catalog.Definition(s_observedWeapon, weapon);
                return contact with { ObservedWeapon = observed.Kind, CapacitorWeaponObserved = observed.UsesCapacitor };
            })
            .ToArray();
        if (!seen.Any(contact => contact.ShipId == primary.ShipId))
        {
            var hull = database.Catalog.Items.GetValueOrDefault(primary.TypeId);
            seen =
            [
                .. seen,
                new(
                    primary.PilotId,
                    primary.ShipId,
                    primary.TypeId,
                    (Eden.Gameplay.Ships.HullClass)(hull?.GroupId ?? 0),
                    0,
                    null,
                    primary.Position,
                    primary.Velocity,
                    (float)primary.Radius
                ),
            ];
        }
        seen = seen.Where(contact => Allowed(contact.ShipId, contact.Position, contact.Radius)).ToArray();
        int limit = Math.Max(0, checked((int)profile.ShipAttributes.GetValueOrDefault(DogmaAttributeId.MaxLockedTargets)));
        HashSet<long> reserved = [];
        if (Allowed(primary.ShipId, primary.Position, primary.Radius))
        {
            reserved.Add(primary.ShipId);
        }
        Dictionary<DogmaEffect, HashSet<long>> claims = [];
        if (gang is not null && live is not null)
        {
            foreach (
                var member in gang.Members.Where(member =>
                    member.PilotId != pilot.PilotId && member.Status == RoamingGangMemberStatus.Active
                )
            )
            {
                foreach (var cycle in live.HaulerCheckpoints.GetValueOrDefault(member.PilotId)?.Work.Defense?.Cycles ?? [])
                {
                    if (cycle.ReadyAt <= now || !PvpSupport.Independent(cycle.Effect))
                    {
                        continue;
                    }
                    if (cycle.Effect == DogmaEffect.PlayerEcm && !cycle.ElectronicWarfare.Any(contact => contact.Applied is not null))
                    {
                        continue;
                    }
                    if (!claims.TryGetValue(cycle.Effect, out var targets))
                    {
                        claims[cycle.Effect] = targets = [];
                    }
                    targets.Add(cycle.TargetId);
                }
            }
        }
        Dictionary<long, CapsuleerAttacker?> result = [];
        foreach (var module in profile.Modules.Where(module => PvpSupport.Independent(module.Effect)))
        {
            if (!claims.TryGetValue(module.Effect, out var assigned))
            {
                claims[module.Effect] = assigned = [];
            }
            long chosen = PvpSupport.Select(
                SupportModule(module),
                pilot.Position!,
                pilot.Flight.ShipRadius,
                seen.Where(contact => reserved.Contains(contact.ShipId) || reserved.Count < limit).ToArray(),
                primary.ShipId,
                state.Cycles.FirstOrDefault(cycle => cycle.ModuleId == module.Item.Id)?.TargetId ?? 0,
                assigned
            );
            var target =
                chosen == 0 ? null
                : chosen == primary.ShipId ? primary
                : PatrolTarget(pilot, chosen, pilot.Position!, now);
            result[module.Item.Id] = target;
            if (target is not null)
            {
                assigned.Add(target.ShipId);
                reserved.Add(target.ShipId);
            }
        }
        return result;
    }
}
