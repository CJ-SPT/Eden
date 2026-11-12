using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Insurance;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat.Npcs;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

internal sealed record ConcordSweep(IReadOnlyList<long> Pilots, long NextExpiry);

[Injectable(InjectionType.Singleton)]
internal sealed class ConcordStore(WorldDatabase database, SqliteWorld lookup, IShipLootRoll lootRoll) : IConcord
{
    public NpcShot? Advance(long pilotId, long now)
    {
        return LiveWorld.Execute(database.Path, () => AdvanceOwned(pilotId, now), nameof(Advance));
    }

    private NpcShot? AdvanceOwned(long pilotId, long now)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        long next = LiveReadModels.Find(database.Path)?.ConcordDeadline(pilotId, () => Deadline(pilotId, now)) ?? Deadline(pilotId, now);
        if (next > now)
        {
            return null;
        }
        using WorldActionScope transaction = new(database, select: world => Participants(world, pilotId));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Find(pilotId)!;
        var response = Pending(world, pilotId);
        response ??= ConcordPersistence.Schedule(world, catalog, pilot, now);
        NpcShot? result = null;
        bool destroyed = false;
        if (response is not null)
        {
            if (pilot.ActiveShipId != response.ShipId || world.Set<ShipLoss>().Any(l => l.ShipId == response.ShipId))
            {
                ConcordPersistence.Complete(world, response, now);
            }
            else if (pilot.ActiveShip.Position is { } position && now >= response.RespondAt)
            {
                if (response.CommanderId is null)
                {
                    Spawn(world, catalog, response, position, now);
                }
                if (now >= response.DestroyAt)
                {
                    var commander = world.Set<WorldNpc>().Find(response.CommanderId!.Value)!;
                    var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
                    var defense = ShipHealth.DefenseFrom(attributes);
                    var health = PilotHealthStore.Read(world, response.ShipId, defense, now);
                    double applied = health.Remaining.Shield + health.Remaining.Armor + health.Remaining.Structure;
                    // CONCORD's terminal consequence is authoritative, independent
                    // of tank, signature, targeting, player EWAR or callback lateness.
                    var source = PilotFittingStore.ReadAttributes(catalog, ConcordRules.CommanderType);
                    var weapon = new NpcWeaponContact(
                        response.ShipId,
                        checked((int)source.GetValueOrDefault(DogmaAttributeId.EntityTurretType)),
                        now,
                        source.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime)
                    );
                    PilotInteractionHistoryStore.Damage(
                        world,
                        response.ShipId,
                        PilotInteractionHistoryStore.Npc(commander, weapon.TurretTypeId),
                        applied
                    );
                    ShipLossPersistence.Apply(
                        world,
                        catalog,
                        pilot,
                        commander.Id,
                        response.ShipId,
                        response.SystemId,
                        position,
                        now,
                        lootRoll
                    );
                    ConcordPersistence.Complete(world, response, now);
                    result = new(
                        commander.Id,
                        commander.TypeId,
                        weapon,
                        DamageVector.None,
                        applied,
                        1,
                        new(response.ShipId, defense, new(0, 0, 0)),
                        null
                    );
                    destroyed = true;
                }
            }
        }
        world.SaveChanges();
        if (destroyed)
        {
            result = result! with { Capsule = lookup.FindPilot(world, pilot.AccountId, pilotId)! };
        }
        transaction.Commit();
        return result;
    }

    private static SavedConcordResponse? Pending(WorldAction world, long pilot) =>
        world.Filter<SavedConcordResponse>(row => row.PilotId == pilot && row.CompletedAt == null).SingleOrDefault();

    private TransactionParticipants Participants(WorldAction world, long pilot)
    {
        long active = world.Pilots.Where(row => row.Id == pilot).Select(row => row.ActiveShipId).Single();
        long ship = Pending(world, pilot)?.ShipId ?? active;
        var npcs = ShipLossPersistence.LossNpcParticipants(world, database.Path, ship);
        return new(
            pilots: [pilot],
            ships: [active, ship],
            resources: npcs.Select(id => new SimulationResource(SharedSimulationResource.Npc, id))
        );
    }

    private long Deadline(long pilotId, long now) => WorldActionScope.Inspect(database, world => Deadline(world, pilotId, now));

    private long Deadline(WorldAction world, long pilotId, long now)
    {
        var pending = Pending(world, pilotId);
        if (pending is not null)
        {
            return pending.CommanderId is null ? pending.RespondAt : pending.DestroyAt;
        }
        if ((world.Set<SavedCrimewatch>().Find(pilotId)?.CriminalUntil ?? 0) <= now)
        {
            return long.MaxValue;
        }
        var pilot = world.Pilots.Find(pilotId)!;
        using CatalogDbContext catalog = new(database.CatalogPath);
        return
            pilot.ActiveShip.Position is not null
            && catalog.RequireItem(pilot.ActiveShip.TypeId).GroupId != FleetDefinitions.CapsuleGroup
            && CrimewatchPersistence.Security(catalog, CrimewatchPersistence.System(catalog, pilot.ActiveShip), false)
                >= CrimewatchRules.HighSecurity
            ? 0
            : long.MaxValue;
    }

    public void Sweep(long now)
    {
        LiveWorld.Execute(database.Path, () => SweepOwned(now), nameof(Sweep));
    }

    private void SweepOwned(long now)
    {
        // Only disconnected pilots are destroyed here. Connected sessions own
        // their notifications and ship replacement through Advance().
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        var work = LiveReadModels.Find(database.Path)?.ConcordOffline(now, () => Offline(now)) ?? Offline(now);
        foreach (long id in work.Pilots)
        {
            Advance(id, now);
        }
    }

    private ConcordSweep Offline(long now) =>
        WorldActionScope.Inspect(
            database,
            world =>
            {
                var flags = world.Filter<SavedCrimewatch>(c => c.CriminalUntil > now).ToArray();
                var ids = flags
                    .Select(row => row.PilotId)
                    .Concat(world.Filter<SavedConcordResponse>(row => row.CompletedAt == null).Select(row => row.PilotId))
                    .Distinct()
                    .ToArray();
                var pilots = ids.Where(id => world.Set<SavedLogoff>().Find(id) is not null).ToArray();
                return new ConcordSweep(pilots, flags.Select(row => row.CriminalUntil).DefaultIfEmpty(long.MaxValue).Min());
            }
        );

    private static void Spawn(
        WorldAction world,
        CatalogDbContext catalog,
        SavedConcordResponse response,
        Eden.Data.World.Docking.FlightPosition position,
        long now
    )
    {
        for (int slot = 0; slot <= ConcordRules.CaptainCount; slot++)
        {
            int type = slot == ConcordRules.CaptainCount ? ConcordRules.CommanderType : ConcordRules.CaptainType;
            var attributes = PilotFittingStore.ReadAttributes(catalog, type);
            var npc = world
                .Add(
                    new WorldNpc
                    {
                        SystemId = response.SystemId,
                        TypeId = type,
                        Role = NpcRole.Concord,
                        EncounterId = response.ShipId,
                        SpawnSlot = slot,
                        CorporationId = ConcordRules.Corporation,
                        FactionId = ConcordRules.Faction,
                        X = position.X + (slot + 1) * ConcordRules.FormationSpacing,
                        Y = position.Y,
                        Z = position.Z,
                        HomeX = position.X,
                        HomeY = position.Y,
                        HomeZ = position.Z,
                        ShieldCharge = attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity),
                        UpdatedAt = now,
                    }
                )
                .Entity;
            world.SaveChanges();
            // Captains use the existing catalog weapons and EWAR simulation.
            // The commander is driven by the saved enforcement deadline.
            if (type == ConcordRules.CaptainType)
            {
                world.Add(NpcAggression.Start(npc.Id, response.ShipId, 0, now));
            }
            else
            {
                response.CommanderId = npc.Id;
            }
            if (slot == 0)
            {
                response.FirstCaptainId = npc.Id;
            }
            else if (slot == 1)
            {
                response.SecondCaptainId = npc.Id;
            }
        }
    }
}
