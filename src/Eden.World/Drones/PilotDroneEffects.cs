using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Fittings;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;

namespace Eden.World.Drones;

internal sealed partial class PilotDroneStore
{
    private void AdvanceOffensiveWork(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        WorldDrone drone,
        DroneContact contact,
        long now
    )
    {
        var profile = contact.Profile.Utility!;
        var work = ReadWork(drone);
        var npc = world.Set<WorldNpc>().Find(drone.TargetId);
        var victim = npc is null ? FindVictim(world, catalog, pilot, drone.TargetId, drone.SystemId!.Value, now) : null;
        var targetContact = victim is null ? null : FindContact(world, victim, drone.SystemId!.Value, now);
        bool validNpc =
            npc is not null
            && npc.DestroyedAt is null
            && npc.RetiredAt is null
            && npc.Navigation != NpcNavigationState.Warping
            && npc.SystemId == drone.SystemId;
        if (!validNpc && targetContact is null)
        {
            ClearDroneEffects(world, drone.ItemId);
            return;
        }
        var position = validNpc ? NpcMotion.Position(npc!) : targetContact!.Position;
        var type = validNpc ? npc!.TypeId : victim!.ActiveShip.TypeId;
        double radius = catalog.FittingType(type)?.Radius ?? 0;
        if (contact.Position.DistanceTo(position) - radius - contact.Profile.Radius > profile.Range)
        {
            ClearDroneEffects(world, drone.ItemId);
            return;
        }
        if (work.DueAt > now)
        {
            return;
        }
        AggressionDecision? decision = null;
        try
        {
            if (victim is not null)
            {
                decision = CrimewatchPersistence.CheckPlayer(world, catalog, pilot, victim, drone.SystemId!.Value, now);
            }
        }
        catch (InvalidDataException)
        {
            ClearDroneEffects(world, drone.ItemId);
            return;
        }
        var attributes = validNpc
            ? PilotFittingStore.ReadAttributes(catalog, type)
            : PilotFittingStore.ShipAttributes(world, catalog, victim!);
        long end = checked(now + (long)Math.Ceiling(profile.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        long expires = checked(now + (long)Math.Ceiling(profile.EffectMilliseconds * TimeSpan.TicksPerMillisecond));
        ClearDroneEffects(world, drone.ItemId);
        var target = new TackleTarget(drone.TargetId, validNpc ? TackleTargetKind.Npc : TackleTargetKind.Ship);
        if (profile.ElectronicWarfare is { } template)
        {
            var module = template with { ItemId = drone.ItemId, TypeId = contact.Profile.TypeId };
            var applied = PlayerEwarRules.Attempt(module, 0, attributes, DrawWork());
            ElectronicWarfareContact effect = new(pilot.Id, drone.ItemId, module, target, applied, now, expires);
            world.Add(
                new WorldPlayerEwar
                {
                    ModuleId = drone.ItemId,
                    PilotId = pilot.Id,
                    SourceShipId = pilot.ActiveShipId,
                    SystemId = drone.SystemId!.Value,
                    TargetId = drone.TargetId,
                    TargetKind = target.Kind,
                    ContactJson = JsonSerializer.Serialize(effect),
                }
            );
        }
        else if (profile.Kind == UtilityDroneKind.Webifying)
        {
            world.Add(
                new WorldTackle
                {
                    ModuleId = drone.ItemId,
                    PilotId = pilot.Id,
                    SourceShipId = pilot.ActiveShipId,
                    TypeId = contact.Profile.TypeId,
                    SystemId = drone.SystemId!.Value,
                    TargetId = drone.TargetId,
                    TargetKind = target.Kind,
                    Effect = DogmaEffect.StasisWebifier,
                    DurationMilliseconds = profile.DurationMilliseconds,
                    Range = profile.Range,
                    WebPercent = profile.Amount,
                    StartedAt = now,
                    ExpiresAt = expires,
                }
            );
        }
        else if (validNpc)
        {
            var energy = world.Set<WorldNpcEnergy>().Find(npc!.Id);
            var pool = NpcCapacitorRules.Read(NpcCapacitorRules.Profile(attributes), energy?.State(), now);
            energy ??= world.Add(new WorldNpcEnergy { NpcId = npc.Id }).Entity;
            energy.Charge = Math.Max(
                0,
                pool.Charge - profile.Amount * attributes.GetValueOrDefault(DogmaAttributeId.EnergyWarfareResistance, 1)
            );
            energy.UpdatedAt = now;
        }
        else
        {
            double capacitor = ShipResources.Capacitor(world, drone.TargetId, attributes, now);
            var energy = world.Set<ShipEnergy>().Find(drone.TargetId) ?? world.Add(new ShipEnergy { ShipId = drone.TargetId }).Entity;
            energy.Charge = Math.Max(
                0,
                capacitor - profile.Amount * attributes.GetValueOrDefault(DogmaAttributeId.EnergyWarfareResistance, 1)
            );
            energy.UpdatedAt = now;
            world.AfterCommit(() => capacitorEvents.Publish(new(victim!.Id, victim.ActiveShipId)));
        }
        if (validNpc)
        {
            CrimewatchPersistence.Npc(world, pilot.Id, drone.SystemId!.Value, end);
            NpcHostility.Record(world, catalog, npc!, pilot.ActiveShipId, now);
        }
        else
        {
            CrimewatchPersistence.Player(world, catalog, pilot.Id, victim!.Id, drone.SystemId!.Value, decision!, now, end);
            DroneCombatPersistence.Incoming(world, victim.ActiveShipId, pilot.ActiveShipId, now);
        }
        WriteWork(drone, work with { TargetId = drone.TargetId, StartedAt = now, DueAt = end });
    }

    // Timed drone effects have no connection-owned fitted-module repeat row.
    // Their lifetime is checked by the existing EWAR/tackle sweep owners.
    internal static bool ActiveEffect(WorldAction world, long droneId, long ship, long target, int system, long expires, long now) =>
        expires > now
        && world.Set<WorldDrone>().Find(droneId) is { } drone
        && drone.ControllerShipId == ship
        && drone.SystemId == system
        && drone.TargetId == target
        && drone.Activity == DroneActivity.Fighting;
}
