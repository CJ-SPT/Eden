using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Mining;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Mining;

[Injectable(InjectionType.Singleton)]
internal sealed partial class NpcMiningSeeder(NpcMiningCatalog profiles)
{
    internal void Seed(WorldDbContext world, CatalogDbContext catalog, int systemId, ClientSystems system, double security, long now)
    {
        if (!NpcConvoyRules.Eligible(security) || system.FactionID is not long faction)
        {
            return;
        }
        var owners = profiles.Owners.Where(o => o.FactionId == faction).ToArray();
        if (owners.Length == 0)
        {
            return;
        }
        long id = NpcMiningRules.EncounterId(systemId);
        var operation = world.Set<WorldNpcMiningOperation>().Find(id);
        var encounter = world.Set<WorldNpcEncounter>().Find(id);
        if (encounter?.NextSpawnAt > now)
        {
            return;
        }
        if (operation is null)
        {
            AsteroidFieldStore.Populate(world, catalog, systemId, now);
            world.SaveChanges();
            var rock = world
                .Set<WorldAsteroid>()
                .Where(a => a.SystemId == systemId && a.BeltId > 0 && a.Quantity > 0)
                .OrderBy(a => a.Id)
                .FirstOrDefault();
            if (rock is null)
            {
                return;
            }
            var owner = owners[(int)(NpcSpawnRules.Seed(systemId, id) % (uint)owners.Length)];
            encounter = new()
            {
                Id = id,
                SystemId = systemId,
                AnchorId = rock.BeltId,
                AnchorKind = NpcAnchorKind.Belt,
                Role = NpcRole.MiningMiner,
                FactionId = owner.FactionId,
                Occupied = true,
                HomeX = rock.X,
                HomeY = rock.Y,
                HomeZ = rock.Z,
                Version = NpcSpawnRules.PopulationVersion,
            };
            operation = new()
            {
                EncounterId = id,
                SystemId = systemId,
                BeltId = rock.BeltId,
                CorporationId = owner.CorporationId,
                FactionId = owner.FactionId,
                StationId = catalog.StationsInSystem(systemId).OrderBy(s => s.Id).Select(s => (int?)s.Id).FirstOrDefault(),
            };
            world.Add(encounter);
            world.Add(operation);
            world.SaveChanges();
            SpawnMiners(world, catalog, operation, encounter, rock, now);
        }
        encounter!.NextSpawnAt = checked(now + TimeSpan.TicksPerSecond);
        var active = world
            .Set<WorldNpc>()
            .Where(n => n.EncounterId == id && n.DestroyedAt == null && n.RetiredAt == null)
            .OrderBy(n => n.Id)
            .ToArray();
        var activeIds = active.Select(n => n.Id).ToArray();
        var members = world.Set<WorldNpcMiningMember>().Where(m => activeIds.Contains(m.NpcId)).ToArray();
        if (operation.ResetAt is long reset && now >= reset)
        {
            foreach (var npc in active)
            {
                npc.RetiredAt = now;
                var aggression = world.Set<NpcAggression>().Find(npc.Id);
                if (aggression is not null)
                {
                    world.Remove(aggression);
                }
            }
            world.SaveChanges();
            if (operation.Stage == NpcMiningStage.Alarm)
            {
                operation.Stage = NpcMiningStage.Cooldown;
                operation.ResetAt = checked(now + NpcMiningRules.CooldownSeconds * TimeSpan.TicksPerSecond);
                encounter.NextSpawnAt = operation.ResetAt;
                return;
            }
            var rock = world
                .Set<WorldAsteroid>()
                .Where(a => a.SystemId == systemId && a.BeltId > 0 && a.Quantity > 0)
                .OrderBy(a => a.Id)
                .FirstOrDefault();
            if (rock is null)
            {
                encounter.NextSpawnAt = checked(now + NpcMiningRules.CooldownSeconds * TimeSpan.TicksPerSecond);
                operation.ResetAt = encounter.NextSpawnAt;
                return;
            }
            encounter.Generation = checked(encounter.Generation + 1);
            operation.BeltId = rock.BeltId;
            encounter.AnchorId = rock.BeltId;
            SpawnMiners(world, catalog, operation, encounter, rock, now);
            return;
        }
        DetectThreat(world, operation, active, now);
        foreach (var npc in active)
        {
            var member = members.Single(m => m.NpcId == npc.Id);
            var hull = profiles.Hull(catalog, npc.TypeId)!;
            SettleTransit(npc, member, hull, now);
            if (npc.RetiredAt is not null)
            {
                foreach (var cargo in world.Set<WorldNpcMiningCargo>().Where(c => c.NpcId == npc.Id))
                {
                    cargo.Quantity = 0;
                }
                continue;
            }
            if (
                operation.Stage == NpcMiningStage.Alarm
                && NpcPopulationRules.IsCivilian(npc.Role)
                && member.WarpStartedAt is null
                && member.Travel is not (NpcMiningTravel.Fleeing or NpcMiningTravel.Safe)
            )
            {
                member.MiningDueAt = null;
                member.MiningStartedAt = null;
                BeginWarp(
                    world,
                    npc,
                    member,
                    hull,
                    NpcMiningRules.SafePoint(new(operation.AlertX, operation.AlertY, operation.AlertZ)),
                    NpcMiningTravel.Fleeing,
                    now
                );
            }
            if (operation.Stage == NpcMiningStage.Working && npc.Role == NpcRole.MiningMiner)
            {
                Mine(world, catalog, npc, member, hull, now);
            }
            if (npc.Role == NpcRole.MiningHauler)
            {
                Haul(world, catalog, operation, npc, member, hull, active, now);
            }
        }
        if (operation.Stage == NpcMiningStage.Working && now >= operation.PickupAt && !active.Any(n => n.Role == NpcRole.MiningHauler))
        {
            SpawnHauler(world, catalog, operation, active, now);
        }
        if (operation.Stage == NpcMiningStage.Alarm && operation.ResponseAt is long response && now >= response)
        {
            SpawnDefense(world, catalog, operation, now);
            operation.ResponseAt = null;
        }
        Support(world, catalog, operation, active, members, now);
    }

    private void SpawnMiners(
        WorldDbContext world,
        CatalogDbContext catalog,
        WorldNpcMiningOperation operation,
        WorldNpcEncounter encounter,
        WorldAsteroid rock,
        long now
    )
    {
        int[] types = [NpcMiningRules.Venture, NpcMiningRules.Retriever, NpcMiningRules.Skiff];
        int type = types[NpcSpawnRules.Seed(operation.SystemId, encounter.Id, encounter.Generation) % (uint)types.Length];
        var hull = profiles.Hull(catalog, type);
        var roster = NpcMiningRules.Roster(operation.FactionId)!;
        var hauler = profiles.Hull(catalog, roster.Hauler);
        if (hull?.Laser is null || hauler is null)
        {
            return;
        }
        for (int slot = 0; slot < NpcMiningRules.MinerCount; slot++)
        {
            var position = new SpacePosition(
                rock.X + rock.Radius + hull.Hull.Type.Radius!.Value + NpcMiningRules.MinerStandOff,
                rock.Y,
                rock.Z + slot * NpcMiningRules.FormationSpacing
            );
            var npc = Spawn(world, operation, hull, NpcRole.MiningMiner, slot, position, now);
            world.Add(
                new WorldNpcMiningMember
                {
                    NpcId = npc.Id,
                    EncounterId = encounter.Id,
                    AsteroidId = rock.Id,
                    Capacitor = hull.Hull.Attributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity),
                    CapacitorAt = now,
                }
            );
        }
        operation.Stage = NpcMiningStage.Working;
        operation.ThreatShipId = null;
        operation.ResponseAt = null;
        operation.ResetAt = null;
        operation.PickupAt = checked(
            now + (long)NpcMiningRules.PickupMilliseconds(hull.Laser, hull.Capacity, hauler.Capacity) * TimeSpan.TicksPerMillisecond
        );
        world.SaveChanges();
    }

    private static WorldNpc Spawn(
        WorldDbContext world,
        WorldNpcMiningOperation operation,
        NpcMiningHull hull,
        NpcRole role,
        int slot,
        SpacePosition position,
        long now
    )
    {
        WorldNpc npc = new()
        {
            EncounterId = operation.EncounterId,
            SpawnSlot = slot,
            SystemId = operation.SystemId,
            BeltId = operation.BeltId,
            AnchorKind = NpcAnchorKind.Belt,
            TypeId = hull.Hull.Type.Id,
            CorporationId = operation.CorporationId,
            FactionId = operation.FactionId,
            Role = role,
            HomeX = position.X,
            HomeY = position.Y,
            HomeZ = position.Z,
            X = position.X,
            Y = position.Y,
            Z = position.Z,
            ShieldCharge = hull.Hull.Defense.Shield,
            UpdatedAt = now,
            MotionAt = now,
        };
        world.Add(npc);
        world.SaveChanges();
        return npc;
    }

    private static void DetectThreat(WorldDbContext world, WorldNpcMiningOperation operation, WorldNpc[] active, long now)
    {
        if (operation.Stage != NpcMiningStage.Working)
        {
            return;
        }
        var hostiles = world
            .Set<SavedNpcStanding>()
            .Where(s => s.SourceId == operation.CorporationId && s.Value <= NpcMiningRules.HostileStanding)
            .Select(s => s.PilotId)
            .ToArray();
        var positions = world
            .Pilots.Include(p => p.ActiveShip)
                .ThenInclude(s => s.Position)
            .Where(p => hostiles.Contains(p.Id) && p.ActiveShip.Position != null)
            .OrderBy(p => p.Id)
            .ToArray();
        foreach (var pilot in positions)
        {
            var position = pilot.ActiveShip.Position!;
            var threatened = active.FirstOrDefault(n =>
                NpcPopulationRules.IsCivilian(n.Role)
                && n.Navigation != NpcNavigationState.Warping
                && position.SystemId == n.SystemId
                && NpcMotion.Position(n).DistanceTo(new(position.X, position.Y, position.Z)) <= NpcEncounterDefinitions.VisibilityRange
            );
            if (threatened is not null)
            {
                Alert(world, threatened, pilot.ActiveShipId, now);
                return;
            }
        }
    }

    internal static void Alert(WorldDbContext world, WorldNpc attacked, long shipId, long now)
    {
        var operation = world.Set<WorldNpcMiningOperation>().Find(attacked.EncounterId);
        if (operation is null || operation.Stage == NpcMiningStage.Cooldown)
        {
            return;
        }
        if (operation.Stage != NpcMiningStage.Alarm)
        {
            operation.Stage = NpcMiningStage.Alarm;
            operation.ThreatShipId = shipId;
            operation.ResponseAt = checked(now + NpcMiningRules.ResponseSeconds * TimeSpan.TicksPerSecond);
            operation.ResetAt = checked(now + NpcMiningRules.DefenseLifetimeSeconds * TimeSpan.TicksPerSecond);
            (operation.AlertX, operation.AlertY, operation.AlertZ) = (attacked.X, attacked.Y, attacked.Z);
        }
        foreach (
            var defender in world
                .Set<WorldNpc>()
                .Where(n =>
                    n.EncounterId == operation.EncounterId
                    && n.Role == NpcRole.MiningDefender
                    && n.DestroyedAt == null
                    && n.RetiredAt == null
                )
                .ToArray()
        )
        {
            if (world.Set<NpcAggression>().Find(defender.Id) is null)
            {
                world.Add(NpcAggression.Start(defender.Id, shipId, 0, now));
            }
        }
    }

    private static double OreVolume(CatalogDbContext catalog, int type) =>
        catalog.Read(CatalogData.Client.Types, (long)type)?.Volume is > 0 and var volume && double.IsFinite(volume)
            ? volume
            : throw new InvalidDataException("Missing catalog ore volume.");

    private static double CargoVolume(WorldDbContext world, CatalogDbContext catalog, long npc) =>
        world.Set<WorldNpcMiningCargo>().Where(c => c.NpcId == npc).ToArray().Sum(c => c.Quantity * OreVolume(catalog, c.TypeId));

    private static void Mine(
        WorldDbContext world,
        CatalogDbContext catalog,
        WorldNpc npc,
        WorldNpcMiningMember member,
        NpcMiningHull hull,
        long now
    )
    {
        var laser = hull.Laser!;
        var rock = member.AsteroidId is long id ? world.Set<WorldAsteroid>().Find(id) : null;
        if (rock is null || rock.Quantity <= 0)
        {
            member.MiningDueAt = null;
            member.MiningStartedAt = null;
            rock = world.Set<WorldAsteroid>().Where(a => a.BeltId == npc.BeltId && a.Quantity > 0).OrderBy(a => a.Id).FirstOrDefault();
            member.AsteroidId = rock?.Id;
        }
        if (rock is null)
        {
            return;
        }
        double range = NpcMotion.Position(npc).DistanceTo(new(rock.X, rock.Y, rock.Z)) - rock.Radius - hull.Hull.Type.Radius!.Value;
        if (range > laser.Range)
        {
            npc.Navigation = NpcNavigationState.Travelling;
            (npc.HomeX, npc.HomeY, npc.HomeZ) = (
                rock.X + rock.Radius + hull.Hull.Type.Radius.Value + NpcMiningRules.MinerStandOff,
                rock.Y,
                rock.Z
            );
            member.MiningDueAt = null;
            member.MiningStartedAt = null;
            return;
        }
        npc.Navigation = NpcNavigationState.Idle;
        (npc.VelocityX, npc.VelocityY, npc.VelocityZ) = (0, 0, 0);
        if (member.MiningDueAt is long pending && now < pending)
        {
            return;
        }
        double cargoVolume = CargoVolume(world, catalog, npc.Id);
        if (member.MiningDueAt is not null)
        {
            long quantity = NpcMiningRules.Extract(
                laser.Amount,
                hull.Capacity - cargoVolume,
                OreVolume(catalog, rock.TypeId),
                rock.Quantity
            );
            var cargo =
                world.Set<WorldNpcMiningCargo>().Find(npc.Id, rock.TypeId)
                ?? world.Add(new WorldNpcMiningCargo { NpcId = npc.Id, TypeId = rock.TypeId }).Entity;
            cargo.Quantity = checked(cargo.Quantity + quantity);
            rock.Quantity -= quantity;
            cargoVolume += quantity * OreVolume(catalog, rock.TypeId);
            member.MiningDueAt = null;
            member.MiningStartedAt = null;
            world.SaveChanges();
        }
        if (cargoVolume >= hull.Capacity || rock.Quantity <= 0)
        {
            return;
        }
        var attributes = hull.Hull.Attributes;
        double capacitor = ChargedResource.Recharged(
            member.Capacitor,
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity),
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorRechargeTime),
            Math.Max(0, (now - member.CapacitorAt) / (double)TimeSpan.TicksPerMillisecond)
        );
        if (capacitor < laser.Capacitor)
        {
            return;
        }
        member.Capacitor = capacitor - laser.Capacitor;
        member.CapacitorAt = now;
        member.MiningStartedAt = now;
        member.MiningDueAt = checked(now + (long)laser.Duration * TimeSpan.TicksPerMillisecond);
    }
}
