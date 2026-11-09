using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Fittings;
using Eden.Data.World.Propulsion;
using Eden.Data.World.Repair;
using Eden.Gameplay.Combat;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Configuration;
using Eden.World.Docking;
using Eden.World.Drones;
using Eden.World.Economy;
using Eden.World.Fittings;
using Eden.World.Market;
using Eden.World.Mining;
using Eden.World.Propulsion;
using Eden.World.Repair;
using Eden.World.Runtime.Combat;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage;

public static class WorldInitialization
{
    public static void Initialize(
        WorldDatabase database,
        TimeProvider? clock = null,
        Eden.Gameplay.Market.WorldEconomySettings? economy = null,
        Eden.Gameplay.Economy.EconomicConfiguration? economics = null
    )
    {
        if (Eden.World.Runtime.LiveWorld.Find(database.Path) is not null)
        {
            throw new InvalidOperationException("Startup reconciliation requires a stopped world.");
        }
        Eden.Gameplay.Market.NpcSupplyRules.ValidateStock(economy?.InstitutionalStock ?? Eden.Gameplay.Market.NpcSupplyRules.Stock);
        if (economics is not null)
        {
            Eden.Gameplay.Economy.EconomicRules.Validate(economics);
        }
        bool skillsChanged = Eden.World.Skills.SkillPackageActivation.Activate(database);
        if (!File.Exists(database.CatalogPath))
        {
            throw new FileNotFoundException(
                "Build the local game catalog with tools/catalog/build.py before starting Eden.",
                database.CatalogPath
            );
        }
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (economics is not null)
        {
            EconomicStore.CheckCatalog(catalog, economics);
        }
        using (SessionPerformance.Phase("startup.schema"))
        {
            WorldMigration.Open(world, catalog, clock ?? TimeProvider.System, economy, economics);
        }
        if (skillsChanged)
        {
            world.Set<Eden.Data.World.Market.MarketStation>().ExecuteUpdate(setters => setters.SetProperty(row => row.SeedVersion, 0));
            int[] retired = database
                .PublishedSkills.Values.Where(skill => skill.Deleted || !skill.Definition.Published)
                .Select(skill => skill.Definition.TypeId)
                .ToArray();
            world
                .Set<Eden.Data.World.Market.NpcMarketOrder>()
                .Where(order => retired.Contains(order.TypeId))
                .ExecuteUpdate(setters => setters.SetProperty(order => order.Remaining, 0).SetProperty(order => order.Issued, 0));
            Eden.World.Skills.SkillPackageActivation.CompleteActivation(database);
        }
        database.LoadTraffic();
        WorldConfigurationStore.Activate(database);
        world.ChangeTracker.Clear();
        using (var reconciliation = world.Database.BeginTransaction())
        {
            EconomicStore.ReconcileBelts(world, (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc());
            reconciliation.Commit();
        }
        using (SessionPerformance.Phase("startup.beltRenewal"))
        {
            while (new ResourceRenewalStore(database, clock ?? TimeProvider.System).Renew() > 0)
            {
                // Reconcile overdue catalog belts before admitting connections.
            }
        }
        var market = new PilotMarketStore(database, clock ?? TimeProvider.System, new MarketChangeSignal());
        using (SessionPerformance.Phase("startup.marketExpiry"))
        {
            while (market.Expire() > 0)
            {
                // Reconcile all overdue escrow once before admitting connections.
            }
        }
        // Drones remain where they were left; downtime releases control and never replays damage.
        world.Set<WorldDroneCombatTriggers>().ExecuteDelete();
        world
            .Set<WorldDrone>()
            .ExecuteUpdate(set =>
                set.SetProperty(d => d.ControllerShipId, 0)
                    .SetProperty(d => d.TargetId, 0)
                    .SetProperty(d => d.Activity, Eden.Gameplay.Drones.DroneActivity.Idle)
                    .SetProperty(d => d.VelocityX, 0)
                    .SetProperty(d => d.VelocityY, 0)
                    .SetProperty(d => d.VelocityZ, 0)
                    .SetProperty(d => d.SpeedLimit, 0)
                    .SetProperty(d => d.Orbiting, false)
                    .SetProperty(d => d.ShotAt, 0)
                    .SetProperty(d => d.ShotTargetId, 0)
                    .SetProperty(d => d.DelegationMode, Eden.Gameplay.Drones.DroneDelegationMode.None)
                    .SetProperty(d => d.DelegatePilotId, 0)
                    .SetProperty(d => d.DelegateShipId, 0)
                    .SetProperty(d => d.DelegateFleetId, 0)
                    .SetProperty(d => d.OwnerMembership, 0)
                    .SetProperty(d => d.DelegateMembership, 0)
                    .SetProperty(d => d.ReactionRevision, 0)
            );
        // Live module bonuses belong to a connection. A restart retains paid
        // capacitor/cooldowns but never leaves an orphaned active bonus.
        world.Set<ActiveFittedModule>().ExecuteDelete();
        world.Set<WorldTackle>().ExecuteDelete();
        Eden.World.Exploration.HackingSchema.Reconcile(world);
        world.Set<WorldPlayerEwar>().ExecuteDelete();
        world.Set<WorldRemoteRepair>().ExecuteDelete();
        world.Set<WorldRemoteAssistance>().ExecuteDelete();
        // NPC target ownership is connection activity, not offline combat.
        world.Set<WorldNpcEwar>().ExecuteDelete();
        world.Set<NpcAggression>().ExecuteDelete();
        // Downtime never replays active NPC repair cycles. Saved health and the
        // notification revision survive; damaged layers start a fresh interval.
        world
            .Set<WorldNpc>()
            .Where(n => n.ShieldRepairAt != 0 || n.ArmorRepairAt != 0)
            .ExecuteUpdate(set => set.SetProperty(n => n.ShieldRepairAt, 0).SetProperty(n => n.ArmorRepairAt, 0));
        // Downtime cancels live projectiles without refunding ammunition or
        // cooldowns. Saved impacts and rewards are never applied again.
        long now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc();
        PilotCrimewatchStore.Reconcile(world, catalog, now);
        world
            .Set<WorldMissile>()
            .Where(m => m.Status == MissileFlightStatus.Flying)
            .ExecuteUpdate(set => set.SetProperty(m => m.Status, MissileFlightStatus.Expired).SetProperty(m => m.EndedAt, now));
        LiveCombatRuntime.Find(database.Path)?.WarmDrones();
    }

    // Explicit development/test open: validate and migrate before creating a reader.
    public static SqliteWorld Open(string path) => Open(new WorldDatabase(path));

    public static SqliteWorld Open(WorldDatabase database)
    {
        Initialize(database);
        return new(database);
    }
}
