using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Mining;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Propulsion;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Mining;

internal sealed partial class NpcMiningSeeder
{
    private void SpawnHauler(WorldDbContext world, CatalogDbContext catalog, WorldNpcMiningOperation operation, WorldNpc[] active, long now)
    {
        var miner = active.FirstOrDefault(n => n.Role == NpcRole.MiningMiner);
        if (miner is null)
        {
            return;
        }
        var roster = NpcMiningRules.Roster(operation.FactionId)!;
        int type = operation.HaulGeneration % 2 == 0 ? roster.Hauler : roster.Transport;
        var hull = profiles.Hull(catalog, type);
        if (hull is null)
        {
            return;
        }
        operation.HaulGeneration = checked(operation.HaulGeneration + 1);
        var destination = NpcMotion.Position(miner) + new SpacePosition(0, NpcMiningRules.HaulerStandOff, 0);
        var origin = StationPoint(catalog, operation) ?? NpcMiningRules.SafePoint(destination);
        var npc = Spawn(world, operation, hull, NpcRole.MiningHauler, NpcMiningRules.MinerCount, origin, now);
        WorldNpcMiningMember member = new() { NpcId = npc.Id, EncounterId = operation.EncounterId };
        world.Add(member);
        BeginWarp(world, npc, member, hull, destination, NpcMiningTravel.Inbound, now);
    }

    private static SpacePosition? StationPoint(CatalogDbContext catalog, WorldNpcMiningOperation operation)
    {
        var station = operation.StationId is int id ? catalog.Set<Station>().SingleOrDefault(s => s.Id == id) : null;
        if (station is null || catalog.FittingType(station.TypeId) is not { } type)
        {
            return null;
        }
        return NpcGuardRules.Home(
            new(station.X, station.Y, station.Z),
            type.Radius ?? 0,
            StationUndockCatalog.Read(catalog, station.TypeId),
            [new(new(station.X, station.Y, station.Z), type.Radius ?? 0)]
        );
    }

    private void Haul(
        WorldDbContext world,
        CatalogDbContext catalog,
        WorldNpcMiningOperation operation,
        WorldNpc npc,
        WorldNpcMiningMember member,
        NpcMiningHull hull,
        WorldNpc[] active,
        long now
    )
    {
        if (
            member.WarpStartedAt is not null
            || member.Travel is not NpcMiningTravel.Collecting
            || operation.Stage != NpcMiningStage.Working
        )
        {
            return;
        }
        var miners = active
            .Where(n => n.Role == NpcRole.MiningMiner && n.Navigation != NpcNavigationState.Warping)
            .OrderBy(n => NpcMotion.Position(n).DistanceTo(NpcMotion.Position(npc)))
            .ToArray();
        foreach (var miner in miners)
        {
            var cargo = world.Set<WorldNpcMiningCargo>().Where(c => c.NpcId == miner.Id && c.Quantity > 0).OrderBy(c => c.TypeId).ToArray();
            if (cargo.Length == 0)
            {
                continue;
            }
            if (NpcMotion.Position(npc).DistanceTo(NpcMotion.Position(miner)) > NpcMiningRules.TransferRange)
            {
                npc.Navigation = NpcNavigationState.Travelling;
                (npc.HomeX, npc.HomeY, npc.HomeZ) = (miner.X, miner.Y + NpcMiningRules.TransferRange * 0.5, miner.Z);
                return;
            }
            foreach (var stack in cargo)
            {
                long amount = NpcMiningRules.Extract(
                    hull.Capacity,
                    hull.Capacity - CargoVolume(world, catalog, npc.Id),
                    OreVolume(catalog, stack.TypeId),
                    stack.Quantity
                );
                var target =
                    world.Set<WorldNpcMiningCargo>().Find(npc.Id, stack.TypeId)
                    ?? world.Add(new WorldNpcMiningCargo { NpcId = npc.Id, TypeId = stack.TypeId }).Entity;
                stack.Quantity -= amount;
                target.Quantity = checked(target.Quantity + amount);
                world.SaveChanges();
            }
            if (CargoVolume(world, catalog, npc.Id) >= hull.Capacity)
            {
                break;
            }
        }
        var destination = StationPoint(catalog, operation) ?? NpcMiningRules.SafePoint(NpcMotion.Position(npc));
        BeginWarp(world, npc, member, hull, destination, NpcMiningTravel.Outbound, now);
        if (member.Travel == NpcMiningTravel.Outbound)
        {
            var miner = miners.FirstOrDefault();
            var miningHull = miner is null ? null : profiles.Hull(catalog, miner.TypeId);
            operation.PickupAt = checked(
                now
                + (long)(
                    miningHull?.Laser is { } laser
                        ? NpcMiningRules.PickupMilliseconds(laser, miningHull.Capacity, hull.Capacity)
                        : NpcMiningRules.CooldownSeconds * 1000d
                ) * TimeSpan.TicksPerMillisecond
            );
        }
    }

    private static bool BeginWarp(
        WorldDbContext world,
        WorldNpc npc,
        WorldNpcMiningMember member,
        NpcMiningHull hull,
        SpacePosition destination,
        NpcMiningTravel travel,
        long now
    )
    {
        var disruption = world
            .Set<WorldTackle>()
            .Where(t => t.TargetId == npc.Id && t.TargetKind == TackleTargetKind.Npc && t.ExpiresAt > now)
            .Select(t => t.Strength)
            .ToArray();
        if (!NpcMiningRules.CanWarp(hull.Hull.Attributes.GetValueOrDefault(DogmaAttributeId.WarpScrambleStatus), disruption))
        {
            return false;
        }
        var origin = NpcMotion.Position(npc);
        // Attribute 600's pinned default is 3 AU/s when a behavior hull omits it.
        double speed = hull.Hull.Attributes.GetValueOrDefault(DogmaAttributeId.WarpSpeedMultiplier, NpcMiningRules.DefaultWarpSpeed);
        FlightProperties flight = new(
            0,
            (float)hull.Hull.Type.Radius!.Value,
            hull.Hull.Type.Mass!.Value,
            NpcMotionRules.Agility(hull.Hull.Attributes),
            (float)hull.Hull.Attributes[DogmaAttributeId.MaxVelocity],
            speed
        );
        WarpCourse course = new(origin, destination, 0, flight, 0);
        member.WarpStartedAt = now;
        member.WarpArrivesAt = checked(
            now + (long)Math.Ceiling(course.DurationSeconds(Math.Min(flight.MaxSpeed / 2, 100))) * TimeSpan.TicksPerSecond
        );
        member.WarpSpeed = speed;
        (member.WarpOriginX, member.WarpOriginY, member.WarpOriginZ) = (origin.X, origin.Y, origin.Z);
        (npc.HomeX, npc.HomeY, npc.HomeZ) = (destination.X, destination.Y, destination.Z);
        member.Travel = travel;
        member.MiningDueAt = null;
        member.MiningStartedAt = null;
        npc.Navigation = NpcNavigationState.Warping;
        return true;
    }

    private static void SettleTransit(WorldNpc npc, WorldNpcMiningMember member, NpcMiningHull hull, long now)
    {
        if (member.Transit(npc) is not { } warp)
        {
            return;
        }
        if (now < warp.ArrivesAt)
        {
            var course = warp.Course(
                0,
                now,
                new(
                    0,
                    (float)hull.Hull.Type.Radius!.Value,
                    hull.Hull.Type.Mass!.Value,
                    NpcMotionRules.Agility(hull.Hull.Attributes),
                    (float)hull.Hull.Attributes[DogmaAttributeId.MaxVelocity],
                    warp.Speed
                )
            );
            var sample = course.Sample(0, Math.Min(hull.Hull.Attributes[DogmaAttributeId.MaxVelocity] / 2, 100));
            (npc.X, npc.Y, npc.Z) = (sample.Position.X, sample.Position.Y, sample.Position.Z);
            (npc.VelocityX, npc.VelocityY, npc.VelocityZ) = (sample.Velocity.X, sample.Velocity.Y, sample.Velocity.Z);
            return;
        }
        (npc.X, npc.Y, npc.Z) = (warp.Destination.X, warp.Destination.Y, warp.Destination.Z);
        (npc.VelocityX, npc.VelocityY, npc.VelocityZ) = (0, 0, 0);
        npc.Navigation = NpcNavigationState.Idle;
        member.WarpStartedAt = null;
        member.WarpArrivesAt = null;
        if (member.Travel == NpcMiningTravel.Outbound)
        {
            npc.RetiredAt = now;
        }
        member.Travel =
            member.Travel == NpcMiningTravel.Fleeing ? NpcMiningTravel.Safe
            : npc.Role == NpcRole.MiningHauler ? NpcMiningTravel.Collecting
            : NpcMiningTravel.Working;
    }

    private void SpawnDefense(WorldDbContext world, CatalogDbContext catalog, WorldNpcMiningOperation operation, long now)
    {
        var roster = NpcMiningRules.Roster(operation.FactionId)!;
        int[] types =
        [
            roster.Damage,
            roster.Damage,
            roster.Tackle,
            roster.Logistics,
            roster.ElectronicWarfare,
            roster.ElectronicWarfare,
            roster.ElectronicWarfare,
            roster.ElectronicWarfare,
        ];
        var hulls = types.Select(t => profiles.Hull(catalog, t)).ToArray();
        if (hulls.Any(h => h is null))
        {
            return;
        }
        for (int slot = 0; slot < types.Length; slot++)
        {
            var destination = new SpacePosition(
                operation.AlertX + slot * NpcMiningRules.FormationSpacing,
                operation.AlertY + NpcMiningRules.HaulerStandOff,
                operation.AlertZ
            );
            var origin = StationPoint(catalog, operation) ?? NpcMiningRules.SafePoint(destination);
            var hull = hulls[slot]!;
            var npc = Spawn(
                world,
                operation,
                hull,
                slot == NpcMiningRules.LogisticsSlot ? NpcRole.MiningLogistics : NpcRole.MiningDefender,
                NpcMiningRules.MinerCount + 1 + slot,
                origin,
                now
            );
            WorldNpcMiningMember member = new()
            {
                NpcId = npc.Id,
                EncounterId = operation.EncounterId,
                Capacitor = hull.Hull.Attributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity),
                CapacitorAt = now,
            };
            world.Add(member);
            BeginWarp(world, npc, member, hull, destination, NpcMiningTravel.Inbound, now);
            if (npc.Role == NpcRole.MiningDefender && operation.ThreatShipId is long target)
            {
                world.Add(NpcAggression.Start(npc.Id, target, 0, now));
            }
        }
    }

    private void Support(
        WorldDbContext world,
        CatalogDbContext catalog,
        WorldNpcMiningOperation operation,
        WorldNpc[] active,
        WorldNpcMiningMember[] members,
        long now
    )
    {
        if (operation.Stage != NpcMiningStage.Alarm)
        {
            return;
        }
        foreach (var healer in active.Where(n => n.Role == NpcRole.MiningLogistics && n.Navigation != NpcNavigationState.Warping))
        {
            var member = members.Single(m => m.NpcId == healer.Id);
            if (now < member.SupportReadyAt)
            {
                continue;
            }
            var profile = profiles.Hull(catalog, healer.TypeId)!.Hull;
            bool armor = profile.Attributes.GetValueOrDefault(DogmaAttributeId.BehaviorArmorRepairDuration) > 0;
            double amount = profile.Attributes.GetValueOrDefault(armor ? DogmaAttributeId.ArmorRepairAmount : DogmaAttributeId.ShieldBonus);
            double duration = profile.Attributes.GetValueOrDefault(
                armor ? DogmaAttributeId.BehaviorArmorRepairDuration : DogmaAttributeId.BehaviorShieldRepairDuration
            );
            double range = profile.Attributes.GetValueOrDefault(
                armor ? DogmaAttributeId.BehaviorArmorRepairRange : DogmaAttributeId.BehaviorShieldRepairRange
            );
            double falloff = profile.Attributes.GetValueOrDefault(
                armor ? DogmaAttributeId.BehaviorArmorRepairFalloff : DogmaAttributeId.BehaviorShieldRepairFalloff
            );
            var target = active
                .Where(n => n.Navigation != NpcNavigationState.Warping && n.Id != healer.Id)
                .Where(n => NpcMotion.Position(n).DistanceTo(NpcMotion.Position(healer)) <= NpcEncounterDefinitions.VisibilityRange)
                .Select(n => new { Npc = n, Defense = profiles.Hull(catalog, n.TypeId)!.Hull.Defense })
                .Where(t => armor ? t.Npc.ArmorDamage > 0 : t.Npc.ShieldCharge < t.Defense.Shield)
                .OrderByDescending(t => armor ? t.Npc.ArmorDamage : t.Defense.Shield - t.Npc.ShieldCharge)
                .FirstOrDefault();
            member.SupportTargetId = null;
            if (target is null || amount <= 0 || duration <= 0)
            {
                continue;
            }
            double cost = profile.Attributes.GetValueOrDefault(
                armor ? DogmaAttributeId.BehaviorArmorRepairCapacitor : DogmaAttributeId.BehaviorShieldRepairCapacitor
            );
            double charge = ChargedResource.Recharged(
                member.Capacitor,
                profile.Attributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity),
                profile.Attributes.GetValueOrDefault(DogmaAttributeId.CapacitorRechargeTime),
                Math.Max(0, (now - member.CapacitorAt) / (double)TimeSpan.TicksPerMillisecond)
            );
            amount = NpcMiningRules.RepairAmount(
                amount,
                range,
                falloff,
                NpcMotion.Position(healer).DistanceTo(NpcMotion.Position(target.Npc))
            );
            if (amount <= 0 || charge < cost)
            {
                continue;
            }
            member.Capacitor = charge - cost;
            member.CapacitorAt = now;
            var npc = target.Npc;
            npc.ShieldCharge = ChargedResource.Recharged(
                npc.ShieldCharge,
                target.Defense.Shield,
                target.Defense.ShieldRechargeMilliseconds,
                Math.Max(0, (now - npc.UpdatedAt) / (double)TimeSpan.TicksPerMillisecond)
            );
            if (armor)
            {
                npc.ArmorDamage = Math.Max(0, npc.ArmorDamage - amount);
            }
            else
            {
                npc.ShieldCharge = Math.Min(target.Defense.Shield, npc.ShieldCharge + amount);
            }
            npc.UpdatedAt = now;
            npc.RepairRevision++;
            member.SupportTargetId = npc.Id;
            member.SupportKind = armor ? NpcActivityKind.ArmorRepair : NpcActivityKind.ShieldRepair;
            member.SupportStartedAt = now;
            member.SupportReadyAt = checked(now + (long)duration * TimeSpan.TicksPerMillisecond);
        }
    }
}
