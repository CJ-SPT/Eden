using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;

namespace Eden.World.Exploration;

internal sealed partial class AbyssalStore
{
    public void Advance()
    {
        long now = Now;
        var runs = Live.Economy.Inspect(world =>
            world.Filter<SavedAbyssalRun>(r => r.Status == (int)AbyssalStatus.Active).Select(r => r.Id).ToArray()
        );
        foreach (long id in runs)
        {
            var owners = Live.Economy.Inspect(world =>
                world.Filter<SavedAbyssalParticipant>(p => p.RunId == id && !p.Finished).Select(p => p.PilotId).ToArray()
            );
            AdvanceDisconnected(id, owners, now);
            int system = Live.Economy.Inspect(world => world.Set<SavedAbyssalRun>().Find(id)!.SystemId);
            using var transaction = Live.Economy.Begin(new(pilots: owners, systems: [system]));
            using CatalogDbContext catalog = new(database.CatalogPath);
            var run = transaction.Set<SavedAbyssalRun>().Find(id)!;
            if (run.Status != (int)AbyssalStatus.Active)
            {
                continue;
            }
            Environment(transaction, catalog, run, now);
            foreach (long pilotId in owners)
            {
                var participant = transaction.Set<SavedAbyssalParticipant>().Find(pilotId)!;
                var pilot = transaction.Pilots.Find(pilotId)!;
                if (run.ExpiresAt <= now || pilot.ActiveShipId != participant.ShipId)
                {
                    Fail(transaction, catalog, run, participant, pilot, now);
                }
            }
            if (transaction.Filter<SavedAbyssalParticipant>(p => p.RunId == id && !p.Finished).Any() == false)
            {
                run.Status = (int)AbyssalStatus.Failed;
                Retire(transaction, run, now);
            }
            transaction.Commit();
        }
    }

    private void AdvanceDisconnected(long runId, IReadOnlyList<long> owners, long now)
    {
        var run = Live.Economy.Inspect(world => world.Set<SavedAbyssalRun>().Find(runId) is { } r ? View(r) : null);
        if (run is null || run.ExpiresAt <= now || run.Status != AbyssalStatus.Active)
        {
            return;
        }
        var candidates = Live.Economy.Inspect(world =>
            owners
                .Select(id => world.Set<SavedAbyssalParticipant>().Find(id)!)
                .Where(p => !p.Finished && world.Pilots.Find(p.PilotId)?.ActiveShipId == p.ShipId)
                .Select(p => new NpcTargetCandidate(p.PilotId, p.ShipId))
                .ToArray()
        );
        var offline = candidates.Where(p => presence?.FindOnline(run.SystemId, p.ShipId) is null).ToArray();
        if (offline.Length == 0)
        {
            return;
        }
        simulation.Advance(run.SystemId, now);
        npcCombat.Acquire(candidates, now);
        const int ProbabilityResolution = 1 << 24;
        foreach (var pilot in offline)
        {
            foreach (long npc in npcCombat.Aggressors(pilot.ShipId))
            {
                double roll =
                    System.Security.Cryptography.RandomNumberGenerator.GetInt32(ProbabilityResolution) / (double)ProbabilityResolution;
                if (npcCombat.Fire(pilot.PilotId, pilot.ShipId, npc, SpacePosition.Zero, now, roll, 0) is { } shot)
                {
                    damageEvents.Publish(
                        new(
                            pilot.PilotId,
                            0,
                            npc,
                            shot.Health,
                            shot.Damage,
                            shot.Applied,
                            shot.Quality,
                            shot.TypeId,
                            false,
                            shot.Capsule,
                            shot.Insurance
                        )
                    );
                    if (shot.Capsule is not null)
                    {
                        break;
                    }
                }
            }
        }
    }

    private void Fail(
        WorldAction world,
        CatalogDbContext catalog,
        SavedAbyssalRun run,
        SavedAbyssalParticipant participant,
        Pilot pilot,
        long now
    )
    {
        // Settlement is one resident transaction, including capsule/implant loss and home-clone replacement.
        // A retry sees Finished and cannot pay insurance or create another replacement.
        if (participant.Finished)
        {
            return;
        }
        participant.Finished = true;
        if (pilot.ActiveShip.Position is not { SystemId: var system } || system != run.SystemId)
        {
            return;
        }
        long originalShip = pilot.ActiveShipId;
        var defense = ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, pilot));
        var attacker = new CombatParty(run.Id, null, 0, 0, AbyssalRules.TraceType, AbyssalRules.TraceType, null, 0);
        List<InventoryChange> changes = [];
        // The first loss creates the capsule; the second invokes existing home-clone/implant settlement.
        for (int body = 0; body < 2 && pilot.ActiveShip.Position is { } position; body++)
        {
            var loss = losses.Apply(world, catalog, pilot, pilot.ActiveShipId, run.SystemId, position, now, attacker);
            changes.AddRange(loss.Changes);
        }
        var replacement = lookup.FindPilot(world, pilot.AccountId, pilot.Id)!;
        var damage = new PilotDamage(
            pilot.Id,
            0,
            run.Id,
            new(originalShip, defense, new(0, 0, 0)),
            new DamageVector(0, 0, 0, 0),
            defense.Shield + defense.Armor + defense.Structure,
            1,
            AbyssalRules.TraceType,
            false,
            replacement,
            null
        )
        {
            InventoryChanges = changes,
        };
        world.AfterCommit(() => damageEvents.Publish(damage));
    }

    internal static void Killed(WorldAction world, CatalogDbContext catalog, WorldNpc npc, Pilot pilot, long now)
    {
        if (
            npc.TypeId is AbyssalEnvironmentRules.ShortTracking or AbyssalEnvironmentRules.MediumTracking
            && world.Set<SavedAbyssalRun>().Find(npc.EncounterId) is { } environment
        )
        {
            environment.DisabledFields |= (int)(AbyssalFieldMask.ShortTracking | AbyssalFieldMask.MediumTracking);
        }
        if (npc.TypeId != AbyssalRules.CacheType)
        {
            return;
        }
        var run = world.Set<SavedAbyssalRun>().Find(npc.EncounterId);
        if (run is null || run.Status != (int)AbyssalStatus.Active || run.ExpiresAt <= now)
        {
            return;
        }
        long id = checked(WreckDefinitions.FirstWreckId + npc.Id - NpcEncounterDefinitions.FirstNpcId);
        world.Add(
            new WorldWreck
            {
                Id = id,
                NpcId = npc.Id,
                OwnerId = pilot.Id,
                CorporationId = pilot.CorporationId,
                TypeId = AbyssalRules.CacheWreckType,
                SystemId = run.SystemId,
                Name = "Triglavian Bioadaptive Cache Wreck",
                X = npc.X,
                Y = npc.Y,
                Z = npc.Z,
                ExpiresAt = run.ExpiresAt,
            }
        );
        // Eden's initial low-tier reward policy. Saved cache destruction and these stacks commit together.
        const int SurveyDatabase = 48121;
        const int ZeroPointCondensate = 48112;
        foreach (var (type, quantity) in new[] { (SurveyDatabase, 1), (ZeroPointCondensate, 2), (run.FilamentTypeId, 1) })
        {
            _ = catalog.RequireItem(type);
            world.Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = pilot.Id,
                    LocationId = id,
                    FlagId = (int)InventoryFlag.Cargo,
                    TypeId = type,
                    Quantity = quantity,
                }
            );
        }
    }
}
