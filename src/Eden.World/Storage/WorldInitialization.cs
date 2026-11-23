using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Fittings;
using Eden.Data.World.Information;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Data.World.Repair;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
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
        Eden.Gameplay.Economy.EconomicConfiguration? economics = null,
        WorldSetupApproval? setup = null
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
        var mods = new Eden.World.Modding.ModStore(database);
        bool modsChanged = mods.BeginActivation();
        bool skillsChanged = modsChanged || (!mods.OwnsCatalog && Eden.World.Skills.SkillPackageActivation.Activate(database));
        string catalogPath = database.CatalogPath;
        if (!File.Exists(catalogPath))
        {
            throw new MissingCatalogException(catalogPath, catalogPath != database.OriginalCatalogPath);
        }
        // Activation can replace the catalog. Capture its resulting generation and
        // reuse one read connection throughout reconciliation, including belt batches.
        using var catalogReads = new CatalogReadScope().Begin();
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(catalogPath);
        if (economics is not null)
        {
            EconomicStore.CheckCatalog(catalog, economics);
        }
        using (SessionPerformance.Phase("startup.schema"))
        {
            WorldMigration.Open(world, catalog, clock ?? TimeProvider.System, economy, economics, setup);
            mods.ImportRuntimeState(world);
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
            if (!modsChanged)
            {
                Eden.World.Skills.SkillPackageActivation.CompleteActivation(database);
            }
        }
        database.LoadTraffic();
        WorldConfigurationStore.Activate(database, clock ?? TimeProvider.System, setup);
        Eden.World.Capsuleers.CapsuleerCommunityStore.ReconcileCloneGrade(world);
        Eden.World.Capsuleers.CapsuleerPopulationActivation.Apply(database, clock ?? TimeProvider.System);
        world.ChangeTracker.Clear();
        using (var reconciliation = world.Database.BeginTransaction())
        {
            EconomicStore.ReconcileBelts(world, (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc());
            reconciliation.Commit();
        }
        using (SessionPerformance.Phase("startup.beltRenewal"))
        {
            ResourceRenewalStore.ReconcileStartup(world, catalog, (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc());
        }
        var market = new PilotMarketStore(database, clock ?? TimeProvider.System, new MarketChangeSignal());
        using (SessionPerformance.Phase("startup.marketExpiry"))
        {
            while (market.Expire() > 0)
            {
                // Reconcile all overdue escrow once before admitting connections.
            }
        }
        // Drones remain where they were left. Fighters retain their assigned tube and controller.
        // The simulation advances at most one observed cycle after the resident world reopens.
        world.Set<WorldDroneCombatTriggers>().ExecuteDelete();
        var fighterItems = world
            .Set<OwnedItem>()
            .Where(item =>
                item.FlagId >= Eden.Gameplay.Flight.FighterDeckRules.FirstTube
                && item.FlagId < Eden.Gameplay.Flight.FighterDeckRules.FirstTube + Eden.Gameplay.Flight.FighterDeckRules.MaximumTubes
            )
            .Select(item => item.Id);
        world
            .Set<WorldDrone>()
            .Where(row => !fighterItems.Contains(row.ItemId))
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
        // A restarted world has no structure control leases. Recover deployed Standup fighters to their tubes.
        var structureHosts = world.Set<SavedStructure>().Select(row => row.Id);
        world
            .Set<WorldDrone>()
            .Where(row => row.SystemId != null && structureHosts.Contains(row.ControllerShipId))
            .ExecuteUpdate(set =>
                set.SetProperty(row => row.Activity, Eden.Gameplay.Drones.DroneActivity.ReturningToBay)
                    .SetProperty(row => row.TargetId, row => row.ControllerShipId)
                    .SetProperty(row => row.Orbiting, false)
            );
        // Autonomous salvage and committed capital modes retain their cycles across startup.
        long moduleReopen = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc();
        int[] capitalModes =
        [
            .. Eden.Gameplay.Flight.CapitalModeRules.Types,
            Eden.Gameplay.Fleets.CommandBurstRules.AmarrPhenomena,
            Eden.Gameplay.Fleets.CommandBurstRules.CaldariPhenomena,
            Eden.Gameplay.Fleets.CommandBurstRules.GallentePhenomena,
            Eden.Gameplay.Fleets.CommandBurstRules.MinmatarPhenomena,
        ];
        var committedModes = world
            .Set<OwnedItem>()
            .Where(i =>
                i.Online
                && (
                    capitalModes.Contains(i.TypeId)
                    || i.TypeId >= Eden.Gameplay.Flight.ShipCompressionRules.CapitalAsteroidOre
                        && i.TypeId <= Eden.Gameplay.Flight.ShipCompressionRules.CapitalMoonOre
                )
            )
            .Select(i => i.Id);
        world
            .Set<ActiveFittedModule>()
            .Where(a =>
                !world.Set<SavedInformationCycle>().Any(cycle => cycle.ModuleId == a.ModuleId)
                && (a.ExpiresAt <= moduleReopen || !committedModes.Contains(a.ModuleId))
            )
            .ExecuteDelete();
        world
            .Set<Eden.Data.World.Fleets.WorldCommandBurst>()
            .Where(row => !row.IndustrialProtection && !row.Phenomena || row.ExpiresAt <= moduleReopen)
            .ExecuteDelete();
        PilotSpatialModuleStore.Reconcile(world, (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc());
        world.Set<WorldTackle>().ExecuteDelete();
        world
            .Set<Eden.Data.World.Propulsion.SavedInterdictionField>()
            .Where(row => row.Kind == Eden.Gameplay.Propulsion.InterdictionKind.HeavyField)
            .ExecuteDelete();
        Eden.World.Exploration.HackingSchema.Reconcile(world);
        world.Set<WorldPlayerEwar>().ExecuteDelete();
        world.Set<SavedLanceDisruption>().Where(row => row.EndsAt <= moduleReopen && row.OscillatorEndsAt <= moduleReopen).ExecuteDelete();
        // Retain unexpired fighter bombs and paid superweapon navigation restrictions.
        foreach (var row in world.Set<SavedAreaWeapon>().ToArray())
        {
            var flight = row.Flight();
            if (flight.Module.Effect == DogmaEffect.FighterBombLaunch && row.ResolvesAt > moduleReopen)
            {
                continue;
            }
            long ends = flight.Module.Doomsday is { } charge
                ? Eden.Gameplay.Combat.AreaWeaponRules.Deadline(flight.StartedAt, charge.DriveLockMilliseconds)
                : 0;
            if (ends <= moduleReopen)
            {
                world.Remove(row);
            }
            else
            {
                row.Save(flight with { Active = true, ResolvesAt = ends });
            }
        }
        world.SaveChanges();
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
        Eden.World.Redemption.DeveloperRewardRecovery.Reconcile(world, catalog, now);
        PilotCrimewatchStore.Reconcile(world, catalog, now);
        world
            .Set<WorldMissile>()
            .Where(m => m.Status == MissileFlightStatus.Flying || m.Status == MissileFlightStatus.Attached)
            .ExecuteUpdate(set => set.SetProperty(m => m.Status, MissileFlightStatus.Expired).SetProperty(m => m.EndedAt, now));
    }

    // The host calls this after migration/configuration/renewal and before opening
    // resident simulation or listeners. Offline migration/authoring opens can initialize
    // storage without provisioning pilots.
    public static Eden.Gameplay.Capsuleers.CapsuleerProvisioningTurn PrepareCapsuleers(
        WorldDatabase database,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default,
        Action<Eden.Gameplay.Capsuleers.CapsuleerPreparationProgress>? progress = null,
        int maximumCommunities = int.MaxValue
    ) =>
        new Eden.World.Capsuleers.CapsuleerProvisioner(database, clock ?? TimeProvider.System).Reconcile(
            maximumCommunities: maximumCommunities,
            cancellationToken: cancellationToken,
            progress: progress
        );

    // Explicit development/test open: validate and migrate before creating a reader.
    public static SqliteWorld Open(string path) => Open(new WorldDatabase(path));

    public static SqliteWorld Open(WorldDatabase database)
    {
        Initialize(database);
        return new(database);
    }
}
