using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Registration;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat.Npcs;
using Eden.World.Combat.Weapons;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

[Injectable]
internal sealed partial class PilotMissileStore(
    WorldDatabase database,
    TimeProvider clock,
    SecurityStatusSignal security,
    IPilotCombatPresence presence,
    SqliteWorld lookup,
    IPilotDamageEvents damageEvents,
    PilotLossSettlement losses
) : IPilotMissiles
{
    private const int HistorySeconds = 120;

    public MissileLaunch Launch(
        long pilotId,
        long moduleId,
        long? targetId,
        IReadOnlySet<long> lockedTargets,
        MissileFiringState motion,
        long now
    ) => LaunchVolley(pilotId, moduleId, targetId, lockedTargets, motion, now, false)[0];

    public IReadOnlyList<MissileLaunch> LaunchBank(
        long pilotId,
        long moduleId,
        long? targetId,
        IReadOnlySet<long> lockedTargets,
        MissileFiringState motion,
        long now
    ) => LaunchVolley(pilotId, moduleId, targetId, lockedTargets, motion, now, true);

    internal IReadOnlyList<MissileLaunch> LaunchOverloadedBank(
        long pilotId,
        long moduleId,
        long? targetId,
        IReadOnlySet<long> lockedTargets,
        MissileFiringState motion,
        long now
    ) => LaunchVolley(pilotId, moduleId, targetId, lockedTargets, motion, now, banked: true, overloaded: true);

    private IReadOnlyList<MissileLaunch> LaunchVolley(
        long pilotId,
        long moduleId,
        long? targetId,
        IReadOnlySet<long> lockedTargets,
        MissileFiringState motion,
        long now,
        bool banked,
        bool overloaded = false
    )
    {
        using WorldActionScope transaction = new(database, select: world => LaunchParticipants(world, pilotId, moduleId, targetId, now));
        var world = transaction.World;
        overloaded |= PilotOverloadStore.Selected(world, database.Path, pilotId, moduleId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        var members = banked ? PilotWeaponBankStore.Members(world, pilot.ActiveShipId, moduleId) : [moduleId];
        overloaded |= members.Any(id => PilotOverloadStore.Selected(world, database.Path, pilotId, id));
        var launchers = members.Select(id => ReadLauncher(world, catalog, pilot, id, now)).ToArray();
        if (launchers.Any(l => l.Charge.TypeId != launchers[0].Charge.TypeId))
        {
            throw new InvalidDataException("Load the same ammunition in every grouped launcher.");
        }
        var snapshot = overloaded ? null : world.CachedFittingSnapshot(pilotId, pilot.ActiveShipId, world.FitRevision(pilotId));
        bool cached =
            snapshot?.Missiles is { } missileFit
            && launchers.All(launcher =>
                snapshot.Modules.ContainsKey(launcher.Module.Id) && missileFit.Charges.ContainsKey(launcher.Charge.Id)
            );
        var calculation = cached
            ? null
            : FittingCalculation.Read(world, catalog, pilot, overloadedModules: overloaded ? members.ToHashSet() : null);
        MissileFitting inputs = new(calculation, cached ? snapshot : null);
        List<MissileLaunch> launches = [];
        ShipHeatSnapshot? heat = null;
        foreach (var launcher in launchers)
        {
            launches.Add(LaunchCore(world, catalog, pilot, inputs, launcher, targetId, lockedTargets, motion, now, out heat));
        }
        transaction.Commit();
        if (heat is not null)
        {
            WorldConnections.PublishHeat(world, pilotId, pilot.ActiveShipId, heat);
        }
        return launches;
    }

    private sealed record MissileFitting(FittingCalculation? Calculation, FittingSnapshot? Snapshot);

    private sealed record LoadedLauncher(OwnedItem Module, OwnedItem Charge, CatalogItemDefinition ChargeType);

    private static LoadedLauncher ReadLauncher(WorldAction world, CatalogDbContext catalog, Pilot pilot, long moduleId, long now)
    {
        long pilotId = pilot.Id;
        var module = world.Set<OwnedItem>().Find(moduleId);
        if (
            pilot.ActiveShip.Position is null
            || module is null
            || module.OwnerId != pilotId
            || module.LocationId != pilot.ActiveShipId
            || !module.Singleton
            || !module.Online
            || !InventoryDefinitions.IsHighSlot(module.FlagId)
            || !MissileLauncherCatalog.Supported(catalog, module.TypeId, catalog.FittingType(module.TypeId)!.GroupId)
        )
        {
            throw new InvalidDataException("Use an online fitted missile launcher while in space.");
        }
        var loaded =
            PilotAmmunitionStore.Contents(world, module.Id) ?? throw new InvalidDataException("Load missiles before firing the launcher.");
        var chargeType = catalog.FittingType(loaded.TypeId)!;
        if (loaded.OwnerId != pilotId || !MissileChargeCatalog.Supported(catalog, loaded.TypeId, chargeType.GroupId))
        {
            throw new InvalidDataException("Load supported missile ammunition.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, module.Id, now);
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, loaded.TypeId);
        return new(module, loaded, chargeType);
    }

    private MissileLaunch LaunchCore(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        MissileFitting inputs,
        LoadedLauncher launcher,
        long? targetId,
        IReadOnlySet<long> lockedTargets,
        MissileFiringState motion,
        long now,
        out ShipHeatSnapshot? heat
    )
    {
        var (module, loaded, chargeType) = launcher;
        var position = pilot.ActiveShip.Position!;
        long pilotId = pilot.Id;
        var calculation = inputs.Calculation;
        var attributes =
            inputs.Snapshot?.Modules[module.Id] ?? (IReadOnlyDictionary<DogmaAttributeId, double>)calculation!.Graph.Attributes(module.Id);
        var charge =
            inputs.Snapshot?.Missiles!.Charges[loaded.Id]
            ?? (IReadOnlyDictionary<DogmaAttributeId, double>)calculation!.Graph.Attributes(loaded.Id);
        bool breacher = chargeType.GroupId == (int)MissileChargeGroup.BreacherPod;
        BreacherPayload? pod = null;
        if (breacher)
        {
            world.RequireUncloaked(pilot.ActiveShipId);
            (attributes, charge) = BreacherAttributes(world, catalog, pilotId, attributes, charge);
            pod = MissileChargeCatalog.Breacher(catalog, loaded.TypeId, charge);
        }
        int rounds = checked((int)attributes.GetValueOrDefault(DogmaAttributeId.ChargeRate));
        double cycle = attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime);
        if (
            rounds <= 0
            || rounds != attributes.GetValueOrDefault(DogmaAttributeId.ChargeRate)
            || loaded.Quantity < rounds
            || !MissileDefinitions.CompatibleCharge(attributes, chargeType.GroupId, charge)
            || !double.IsFinite(cycle)
            || cycle <= 0
        )
        {
            throw new InvalidDataException("Load enough compatible missiles before firing.");
        }
        var cooldown = world.Set<ModuleCooldown>().Find(module.Id);
        if (cooldown is not null && cooldown.ReadyAt > now)
        {
            throw new InvalidDataException("The launcher is still cycling or reloading.");
        }
        var defense = inputs.Snapshot?.Missiles!.Defense ?? ShipHealth.DefenseFrom(calculation!.Graph.Attributes(pilot.ActiveShipId));
        if (PilotHealthStore.Read(world, pilot.ActiveShipId, defense, now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot launch missiles.");
        }
        int systemId = position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
        SpacePosition origin = new(position.X, position.Y, position.Z);
        bool automatic = FittingCatalog.DefaultEffect(catalog, loaded.TypeId) == DogmaEffect.AutoTargetingMissile;
        double maximumRange =
            inputs.Snapshot?.Missiles!.MaximumTargetRange ?? calculation!.Graph.Read(pilot.ActiveShipId, DogmaAttributeId.MaxTargetRange);
        if (automatic)
        {
            double autoRange = charge.GetValueOrDefault(DogmaAttributeId.AutoTargetingMissileRange);
            if (!double.IsFinite(autoRange) || autoRange <= 0)
            {
                throw new InvalidDataException("Invalid automatic missile acquisition range.");
            }
            maximumRange = Math.Min(autoRange, NpcEncounterDefinitions.VisibilityRange);
            var aggressors = world.Set<NpcAggression>().Where(a => a.TargetShipId == pilot.ActiveShipId).Select(a => a.NpcId).ToArray();
            var candidates = world
                .Set<WorldNpc>()
                .Where(n =>
                    n.SystemId == systemId
                    && n.DestroyedAt == null
                    && n.RetiredAt == null
                    && n.Navigation != NpcNavigationState.Warping
                    && Enumerable.Contains(aggressors, n.Id)
                )
                .AsEnumerable()
                .Select(n => new MissileAggressor(n.Id, NpcMotion.Position(n)))
                .ToArray();
            var pilotAggressors = world
                .Set<SavedPilotHostility>()
                .Where(h => h.TargetShipId == pilot.ActiveShipId && h.ExpiresAt > now)
                .Select(h => h.SourceShipId)
                .ToArray()
                .Select(id => AutomaticVictim(world, catalog, pilot, id, systemId, now))
                .OfType<Pilot>()
                .Select(victim => FindContact(world, victim, systemId, now))
                .OfType<PilotCombatPresence>()
                .Select(p => new MissileAggressor(p.ShipId, p.Position));
            targetId = MissileRules.AcquireAggressor(origin, maximumRange, candidates.Concat(pilotAggressors).ToArray());
        }
        else if (targetId is not long selected || !lockedTargets.Contains(selected))
        {
            throw new InvalidDataException("Lock a nearby ship before launching missiles.");
        }
        var npc = targetId is long id ? world.Set<WorldNpc>().Find(id) : null;
        Pilot? victim = null;
        AggressionDecision? aggression = null;
        MissileTarget target;
        CatalogItemDefinition targetType;
        if (npc is not null)
        {
            if (
                npc.DestroyedAt.HasValue
                || npc.RetiredAt.HasValue
                || npc.SystemId != systemId
                || npc.Navigation == NpcNavigationState.Warping
            )
            {
                throw new InvalidDataException("The NPC is unavailable.");
            }
            targetType = catalog.FittingType(npc.TypeId)!;
            target = new(NpcMotion.Position(npc), NpcMotion.Velocity(npc), (float)(targetType.Radius ?? 0));
        }
        else
        {
            victim = targetId is long shipId ? FindVictim(world, catalog, systemId, shipId, now) : null;
            var contact = victim is not null ? FindContact(world, victim, systemId, now) : null;
            if (victim is null || victim.Id == pilotId || contact is null)
            {
                throw new InvalidDataException("No eligible attacking ship is available, or the target is unavailable.");
            }
            aggression = CrimewatchPersistence.CheckPlayer(world, catalog, pilot, victim, systemId, now);
            targetType = catalog.FittingType(victim.ActiveShip.TypeId)!;
            target = new(contact.Position, contact.Velocity, (float)(targetType.Radius ?? 0));
        }
        var sourceType = catalog.FittingType(pilot.ActiveShip.TypeId)!;
        if (
            breacher
            && !CatalogTypeLists
                .Read(catalog, BreacherRules.TargetTypeList)
                .Contains(targetType.Id, targetType.GroupId, targetType.CategoryId)
        )
        {
            throw new InvalidDataException("This target cannot receive a Breacher Pod.");
        }
        double distance = Math.Max(0, origin.DistanceTo(target.Position) - (sourceType.Radius ?? 0) - (targetType.Radius ?? 0));
        if (distance > maximumRange || distance > NpcEncounterDefinitions.VisibilityRange)
        {
            throw new InvalidDataException("The target is outside missile targeting range.");
        }
        var drive = MissileChargeCatalog.Drive(catalog, loaded.TypeId, charge);
        var warhead = breacher
            ? new MissileWarhead(DamageVector.None, 1, 1, 1)
            : MissileRules.Warhead(
                charge,
                inputs.Snapshot?.Missiles!.DamageMultiplier ?? calculation!.Graph.Read(pilotId, DogmaAttributeId.MissileDamageMultiplier)
            );
        var flight = MissileFlightRules.Launch(
            drive,
            new(origin, motion.Velocity, motion.MaximumSpeed, motion.RearDirection),
            target,
            charge.GetValueOrDefault(DogmaAttributeId.AimedMissileLaunch) != 0
        );
        MissilePayload payload = new(flight, warhead, pod);
        // Pilot contacts describe the launch instant. Rounding their birth down
        // would backdate moving targets and make interception depend on clock phase.
        long launchBoundary = victim is null && !breacher ? now - now % TimeSpan.TicksPerSecond : now;
        var projectile = world
            .Set<WorldMissile>()
            .Add(
                new()
                {
                    PilotId = pilotId,
                    SourceShipId = pilot.ActiveShipId,
                    SourceTypeId = pilot.ActiveShip.TypeId,
                    ModuleId = module.Id,
                    LauncherTypeId = module.TypeId,
                    ChargeTypeId = loaded.TypeId,
                    GroupId = chargeType.GroupId,
                    SystemId = systemId,
                    TargetId = targetId!.Value,
                    TargetKind = victim is null ? MissileTargetKind.Npc : MissileTargetKind.Pilot,
                    TargetRadius = target.Radius,
                    CorporationId = pilot.CorporationId,
                    LaunchStamp = motion.Stamp,
                    LaunchedAt = launchBoundary,
                    UpdatedAt = launchBoundary,
                    ExpiresAt = checked(launchBoundary + (long)Math.Ceiling(drive.LifetimeMilliseconds * TimeSpan.TicksPerMillisecond)),
                    Snapshot = JsonSerializer.Serialize(payload, MissileJson.Default.MissilePayload),
                }
            )
            .Entity;
        loaded.Quantity -= rounds;
        Magazine magazine = new(module.Id, module.LocationId, module.FlagId, loaded.TypeId, loaded.Quantity, 0);
        if (loaded.Quantity == 0)
        {
            world.Remove(loaded);
        }
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = module.Id }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = checked(now + (long)Math.Ceiling(cycle * TimeSpan.TicksPerMillisecond));
        // Retaliation begins on the offensive launch, not after flight time.
        Eden.World.Drones.DroneCombatPersistence.Module(world, pilot.ActiveShipId, targetId!.Value, victim is not null, now);
        if (victim is not null)
        {
            CrimewatchPersistence.Player(world, catalog, pilotId, victim.Id, systemId, aggression!, now, cooldown.ReadyAt);
        }
        else
        {
            NpcHostility.Record(world, catalog, npc!, pilot.ActiveShipId, now);
            CrimewatchPersistence.Npc(world, pilotId, systemId, cooldown.ReadyAt);
        }
        heat =
            calculation?.OverloadedModules.Contains(module.Id) == true
                ? ShipHeatPersistence.BeginPaidCycle(world, catalog, calculation, module.Id, now, cooldown.ReadyAt)
                : null;
        world.SaveChanges();
        return new(new(module.Id, module.TypeId, loaded.TypeId, cycle, automatic, !breacher), projectile.Contact(payload), magazine);
    }

    public void Advance(int systemId, long now)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("read.pilotMissileWork");
        if (LiveReadModels.Find(database.Path) is { } resident && !resident.HasPilotMissiles(systemId))
        {
            return;
        }
        bool destroyed = false;
        List<PilotDamage> pilotImpacts = [];
        using WorldActionScope transaction = new(database, select: world => FlightParticipants(world, systemId));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var rows = Flying(world, systemId).OrderBy(m => m.LaunchedAt).ThenBy(m => m.Id).ToArray();
        if (rows.Length == 0)
        {
            return;
        }
        foreach (var source in rows.Where(m => m.Status == MissileFlightStatus.Flying).GroupBy(m => (m.PilotId, m.SourceShipId)))
        {
            var pilot = world.Pilots.Find(source.Key.PilotId)!;
            var position = pilot.ActiveShip.Position;
            if (
                pilot.ActiveShipId != source.Key.SourceShipId
                || position is null
                || (position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId) != systemId
                || world.Set<ShipLoss>().Any(l => l.ShipId == source.Key.SourceShipId)
            )
            {
                foreach (var row in source)
                {
                    row.Status = MissileFlightStatus.Expired;
                    row.EndedAt = now;
                }
            }
        }
        // Multiple viewers share this transaction and each saved tick boundary.
        // Missiles whose entire lifetime passed without a tick never replay damage.
        foreach (var expired in rows.Where(m => m.Status == MissileFlightStatus.Flying && now > m.ExpiresAt + TimeSpan.TicksPerSecond))
        {
            expired.Status = MissileFlightStatus.Expired;
            expired.EndedAt = now;
        }
        while (true)
        {
            var due = rows.Where(m =>
                    m.Status == MissileFlightStatus.Flying
                    && now - m.UpdatedAt >= Math.Min(TimeSpan.TicksPerSecond, m.ExpiresAt - m.UpdatedAt)
                )
                .OrderBy(m => m.UpdatedAt)
                .ThenBy(m => m.Id)
                .ToArray();
            if (due.Length == 0)
            {
                break;
            }
            long boundary = due[0].UpdatedAt;
            List<PendingStep> steps = [];
            foreach (var row in due.Where(m => m.UpdatedAt == boundary))
            {
                var payload = row.Payload();
                var npc = row.TargetKind == MissileTargetKind.Npc ? world.Set<WorldNpc>().Find(row.TargetId) : null;
                MissileTarget? target = null;
                if (
                    npc is { DestroyedAt: null, RetiredAt: null }
                    && npc.SystemId == systemId
                    && npc.Navigation != NpcNavigationState.Warping
                )
                {
                    double offset = npc.MotionAt == 0 ? 0 : (row.UpdatedAt - npc.MotionAt) / (double)TimeSpan.TicksPerSecond;
                    target = new(NpcMotion.Position(npc) + NpcMotion.Velocity(npc) * offset, NpcMotion.Velocity(npc), row.TargetRadius);
                }
                if (
                    row.TargetKind == MissileTargetKind.Pilot
                    && FindVictim(world, catalog, systemId, row.TargetId, now) is { } victim
                    && FindContact(world, victim, systemId, now) is { } contact
                )
                {
                    // The contact is current. Reconstruct this step's start for the existing flight integrator.
                    double offset = Math.Min(0, (row.UpdatedAt - now) / (double)TimeSpan.TicksPerSecond);
                    target = new(contact.Position + contact.Velocity * offset, contact.Velocity, row.TargetRadius);
                }
                var next = MissileFlightRules.Tick(payload.Flight, target);
                long time = checked(row.LaunchedAt + (long)Math.Round(next.ElapsedSeconds * TimeSpan.TicksPerSecond));
                steps.Add(new(row, payload with { Flight = next }, npc, time));
            }
            foreach (var step in steps.OrderBy(s => s.Time).ThenBy(s => s.Row.Id))
            {
                var (row, payload, npc, time) = step;
                if (world.Pilots.Find(row.PilotId)!.ActiveShipId != row.SourceShipId)
                {
                    row.Status = MissileFlightStatus.Expired;
                    row.EndedAt = time;
                    continue;
                }
                row.Save(payload);
                row.UpdatedAt = time;
                row.Status = payload.Flight.Status;
                if (row.Status != MissileFlightStatus.Flying)
                {
                    row.EndedAt = time;
                }
                if (row.Status == MissileFlightStatus.Impact && payload.Breacher is not null)
                {
                    // Settle owed ticks before replacing an application at this impact boundary.
                    destroyed |= AdvanceBreacher(world, catalog, rows, systemId, time, pilotImpacts);
                    AttachBreacher(row, payload, time, rows);
                    continue;
                }
                if (row.Status == MissileFlightStatus.Impact && row.TargetKind == MissileTargetKind.Pilot)
                {
                    var victim = FindVictim(world, catalog, systemId, row.TargetId, now);
                    if (victim is not null && FindContact(world, victim, systemId, now) is { } contact)
                    {
                        pilotImpacts.Add(ApplyPilotImpact(world, catalog, row, payload, victim, contact, time));
                    }
                }
                if (
                    row.Status == MissileFlightStatus.Impact
                    && npc is { DestroyedAt: null, RetiredAt: null }
                    && npc.Navigation != NpcNavigationState.Warping
                )
                {
                    var attributes = PlayerEwarStore.NpcAttributes(world, npc.Id, PilotFittingStore.ReadAttributes(catalog, npc.TypeId));
                    double application = MissileRules.Application(
                        payload.Warhead,
                        attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
                        NpcMotion.Velocity(npc).Length
                    );
                    var damage = payload.Warhead.Damage.Scale(application);
                    var pilot = world.Pilots.Find(row.PilotId)!;
                    var result = NpcDamagePersistence.Apply(
                        world,
                        catalog,
                        npc,
                        pilot,
                        row.SourceShipId,
                        attributes,
                        damage,
                        time,
                        row.LauncherTypeId,
                        row.SourceTypeId,
                        paidProjectile: true
                    );
                    destroyed |= result.Destroyed;
                    world.SaveChanges();
                    MissileImpact outcome = new(
                        row.Id,
                        row.PilotId,
                        row.SourceShipId,
                        row.TargetId,
                        row.LauncherTypeId,
                        row.ChargeTypeId,
                        result.Health,
                        damage,
                        result.Applied,
                        result.Destroyed,
                        result.Reward is null ? null : NpcDamagePersistence.Read(result.Reward)
                    );
                    row.Outcome = JsonSerializer.Serialize(outcome, MissileJson.Default.MissileImpact);
                }
            }
        }
        destroyed |= AdvanceBreacher(world, catalog, rows, systemId, now, pilotImpacts);
        long oldest = now - HistorySeconds * TimeSpan.TicksPerSecond;
        world.RemoveRange(world.ExpiredMissileHistory(oldest, npcOnly: false));
        transaction.Commit();
        transaction.Dispose();
        foreach (var impact in pilotImpacts)
        {
            damageEvents.Publish(impact);
        }
        if (destroyed)
        {
            security.Publish();
        }
    }

    private sealed record PendingStep(WorldMissile Row, MissilePayload Payload, WorldNpc? Npc, long Time);

    public IReadOnlyList<MissileContact> List(int systemId)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("read.missileContacts");
        return LiveReadModels.Find(database.Path) is { } resident
            ? resident.MissileContacts(systemId, () => ReadContacts(systemId))
            : ReadContacts(systemId);
    }

    private IReadOnlyList<MissileContact> ReadContacts(int systemId)
    {
        return WorldActionScope.Inspect(
            database,
            world =>
                world
                    .Set<WorldMissile>()
                    .Where(m => m.SystemId == systemId && m.Status == MissileFlightStatus.Flying)
                    .OrderBy(m => m.Id)
                    .AsEnumerable()
                    .Select(m => m.Contact(m.Payload()))
                    .ToArray()
        );
    }

    public bool Impacted(long projectileId)
    {
        if (LiveReadModels.Find(database.Path) is { } resident)
        {
            return resident.Impacted(projectileId);
        }
        return WorldActionScope.Inspect(
            database,
            world =>
                world
                    .Set<WorldMissile>()
                    .Any(m => m.Id == projectileId && (m.Status == MissileFlightStatus.Impact || m.Status == MissileFlightStatus.Attached))
        );
    }

    public IReadOnlyList<MissileImpact> TakeImpacts(long pilotId)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("read.missileReports");
        if (LiveReadModels.Find(database.Path) is { } resident && !resident.HasMissileReports(pilotId))
        {
            return [];
        }
        using WorldActionScope transaction = new(database, TransactionParticipants.None);
        var world = transaction.World;
        var rows = world
            .Set<WorldMissile>()
            .Where(m => m.SourceKind == MissileSourceKind.Pilot && m.PilotId == pilotId && m.Outcome != null && !m.Reported)
            .OrderBy(m => m.EndedAt)
            .ThenBy(m => m.Id)
            .ToArray();
        var results = rows.Select(m =>
                JsonSerializer.Deserialize(m.Outcome!, MissileJson.Default.MissileImpact)
                ?? throw new InvalidDataException("Missing missile impact.")
            )
            .ToArray();
        foreach (var row in rows)
        {
            row.Reported = true;
        }
        world.SaveChanges();
        transaction.Commit();
        return results;
    }

    public void Cancel(long pilotId, long shipId)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId], ships: [shipId]));
        var world = transaction.World;
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        foreach (
            var row in world
                .Set<WorldMissile>()
                .Where(m =>
                    m.SourceKind == MissileSourceKind.Pilot
                    && m.PilotId == pilotId
                    && m.SourceShipId == shipId
                    && m.Status == MissileFlightStatus.Flying
                )
                .ToArray()
        )
        {
            row.Status = MissileFlightStatus.Expired;
            row.EndedAt = now;
        }
        transaction.Commit();
    }
}
