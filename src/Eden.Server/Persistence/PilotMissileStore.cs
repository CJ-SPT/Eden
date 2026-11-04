using System.Text.Json;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotMissileStore(WorldDatabase database, TimeProvider clock, SecurityStatusSignal security) : IPilotMissiles
{
    private const int HistorySeconds = 120;

    public MissileLaunch Launch(
        long pilotId,
        long moduleId,
        long? targetId,
        IReadOnlySet<long> lockedTargets,
        MissileFiringState motion,
        long now
    )
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var module = world.Set<OwnedItem>().Find(moduleId);
        if (
            pilot.ActiveShip.Position is not { } position
            || module is null
            || module.OwnerId != pilotId
            || module.LocationId != pilot.ActiveShipId
            || !module.Singleton
            || !module.Online
            || !InventoryDefinitions.IsHighSlot(module.FlagId)
            || !MissileLauncherCatalog.Supported(catalog, module.TypeId, catalog.Set<ItemType>().Find(module.TypeId)!.GroupId)
        )
        {
            throw new InvalidDataException("Use an online fitted missile launcher while in space.");
        }
        var loaded =
            PilotAmmunitionStore.Contents(world, module.Id) ?? throw new InvalidDataException("Load missiles before firing the launcher.");
        var chargeType = catalog.Set<ItemType>().Find(loaded.TypeId)!;
        if (loaded.OwnerId != pilotId || !MissileChargeCatalog.Supported(catalog, loaded.TypeId, chargeType.GroupId))
        {
            throw new InvalidDataException("Load supported missile ammunition.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, module.TypeId);
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, loaded.TypeId);
        var calculation = FittingCalculation.Read(world, catalog, pilot);
        var attributes = calculation.Graph.Attributes(module.Id);
        var charge = calculation.Graph.Attributes(loaded.Id);
        int rounds = checked((int)attributes.GetValueOrDefault(DogmaAttributeId.ChargeRate));
        double cycle = attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime);
        if (
            rounds <= 0
            || rounds != attributes.GetValueOrDefault(DogmaAttributeId.ChargeRate)
            || loaded.Quantity < rounds
            || !MissileDefinitions.CompatibleCharge(attributes, chargeType.GroupId)
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
        var ship = calculation.Graph.Attributes(pilot.ActiveShipId);
        if (PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot launch missiles.");
        }
        int systemId = position.SystemId ?? catalog.Set<Station>().Single(s => s.Id == pilot.ActiveShip.StationId).SolarSystemId;
        SpacePosition origin = new(position.X, position.Y, position.Z);
        bool automatic = FittingCatalog.DefaultEffect(catalog, loaded.TypeId) == DogmaEffect.AutoTargetingMissile;
        double maximumRange = ship.GetValueOrDefault(DogmaAttributeId.MaxTargetRange);
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
                .Where(n => n.SystemId == systemId && n.DestroyedAt == null && n.RetiredAt == null && aggressors.Contains(n.Id))
                .AsEnumerable()
                .Select(n => new MissileAggressor(n.Id, NpcMotion.Position(n)))
                .ToArray();
            targetId = MissileRules.AcquireAggressor(origin, maximumRange, candidates);
        }
        else if (targetId is not long selected || !lockedTargets.Contains(selected))
        {
            throw new InvalidDataException("Lock a nearby NPC before launching missiles.");
        }
        var npc = targetId is long id ? world.Set<WorldNpc>().Find(id) : null;
        if (npc is null || npc.DestroyedAt.HasValue || npc.RetiredAt.HasValue || npc.SystemId != systemId)
        {
            throw new InvalidDataException(automatic ? "No attacking NPC is available for automatic missiles." : "The NPC is unavailable.");
        }
        var npcType = catalog.Set<ItemType>().Find(npc.TypeId)!;
        var sourceType = catalog.Set<ItemType>().Find(pilot.ActiveShip.TypeId)!;
        double distance = Math.Max(0, origin.DistanceTo(NpcMotion.Position(npc)) - (sourceType.Radius ?? 0) - (npcType.Radius ?? 0));
        if (distance > maximumRange || distance > NpcEncounterDefinitions.VisibilityRange)
        {
            throw new InvalidDataException("The NPC is outside missile targeting range.");
        }
        var drive = MissileChargeCatalog.Drive(catalog, loaded.TypeId, charge);
        var warhead = MissileRules.Warhead(charge, calculation.Graph.Read(pilotId, DogmaAttributeId.MissileDamageMultiplier));
        var flight = MissileFlightRules.Launch(
            drive,
            new(origin, motion.Velocity, motion.MaximumSpeed, motion.RearDirection),
            new(NpcMotion.Position(npc), NpcMotion.Velocity(npc), (float)(npcType.Radius ?? 0)),
            charge.GetValueOrDefault(DogmaAttributeId.AimedMissileLaunch) != 0
        );
        MissilePayload payload = new(flight, warhead);
        long launchBoundary = now - now % TimeSpan.TicksPerSecond;
        var projectile = world
            .Set<WorldMissile>()
            .Add(
                new()
                {
                    PilotId = pilotId,
                    SourceShipId = pilot.ActiveShipId,
                    ModuleId = module.Id,
                    LauncherTypeId = module.TypeId,
                    ChargeTypeId = loaded.TypeId,
                    GroupId = chargeType.GroupId,
                    SystemId = systemId,
                    TargetId = npc.Id,
                    TargetRadius = (float)(npcType.Radius ?? 0),
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
        var npcAttributes = PilotFittingStore.ReadAttributes(catalog, npc.TypeId);
        if (
            world.Set<NpcAggression>().Find(npc.Id) is null
            && npcAttributes.TryGetValue(DogmaAttributeId.EntityAttackDelayMinimum, out double delay)
        )
        {
            world.Set<NpcAggression>().Add(NpcAggression.Start(npc.Id, pilot.ActiveShipId, delay, now));
        }
        world.SaveChanges();
        transaction.Commit();
        return new(new(module.Id, module.TypeId, loaded.TypeId, cycle, automatic), projectile.Contact(payload), magazine);
    }

    public void Advance(int systemId, long now)
    {
        bool destroyed = false;
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var rows = world
            .Set<WorldMissile>()
            .Where(m => m.SystemId == systemId && m.Status == MissileFlightStatus.Flying)
            .OrderBy(m => m.LaunchedAt)
            .ThenBy(m => m.Id)
            .ToArray();
        if (rows.Length == 0)
        {
            return;
        }
        foreach (var source in rows.GroupBy(m => (m.PilotId, m.SourceShipId)))
        {
            var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == source.Key.PilotId);
            var position = pilot.ActiveShip.Position;
            int sourceSystem = position?.SystemId ?? catalog.Set<Station>().Single(s => s.Id == pilot.ActiveShip.StationId).SolarSystemId;
            if (
                pilot.ActiveShipId != source.Key.SourceShipId
                || position is null
                || sourceSystem != systemId
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
                var npc = world.Set<WorldNpc>().Find(row.TargetId);
                MissileTarget? target = null;
                if (npc is { DestroyedAt: null, RetiredAt: null } && npc.SystemId == systemId)
                {
                    double offset = npc.MotionAt == 0 ? 0 : (row.UpdatedAt - npc.MotionAt) / (double)TimeSpan.TicksPerSecond;
                    target = new(NpcMotion.Position(npc) + NpcMotion.Velocity(npc) * offset, NpcMotion.Velocity(npc), row.TargetRadius);
                }
                var next = MissileFlightRules.Tick(payload.Flight, target);
                long time = checked(row.LaunchedAt + (long)Math.Round(next.ElapsedSeconds * TimeSpan.TicksPerSecond));
                steps.Add(new(row, payload with { Flight = next }, npc, time));
            }
            foreach (var step in steps.OrderBy(s => s.Time).ThenBy(s => s.Row.Id))
            {
                var (row, payload, npc, time) = step;
                row.Save(payload);
                row.UpdatedAt = time;
                row.Status = payload.Flight.Status;
                if (row.Status != MissileFlightStatus.Flying)
                {
                    row.EndedAt = time;
                }
                if (row.Status == MissileFlightStatus.Impact && npc is { DestroyedAt: null, RetiredAt: null })
                {
                    var attributes = PilotFittingStore.ReadAttributes(catalog, npc.TypeId);
                    double application = MissileRules.Application(
                        payload.Warhead,
                        attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
                        NpcMotion.Velocity(npc).Length
                    );
                    var damage = payload.Warhead.Damage.Scale(application);
                    var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == row.PilotId);
                    var result = NpcDamagePersistence.Apply(world, catalog, npc, pilot, row.SourceShipId, attributes, damage, time);
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
        long oldest = now - HistorySeconds * TimeSpan.TicksPerSecond;
        world.Set<WorldMissile>().Where(m => m.EndedAt < oldest && (m.Outcome == null || m.Reported)).ExecuteDelete();
        world.SaveChanges();
        transaction.Commit();
        if (destroyed)
        {
            security.Publish();
        }
    }

    private sealed record PendingStep(WorldMissile Row, MissilePayload Payload, WorldNpc? Npc, long Time);

    public IReadOnlyList<MissileContact> List(int systemId)
    {
        using WorldDbContext world = new(database.Path);
        return world
            .Set<WorldMissile>()
            .Where(m => m.SystemId == systemId && m.Status == MissileFlightStatus.Flying)
            .OrderBy(m => m.Id)
            .AsEnumerable()
            .Select(m => m.Contact(m.Payload()))
            .ToArray();
    }

    public bool Impacted(long projectileId)
    {
        using WorldDbContext world = new(database.Path);
        return world.Set<WorldMissile>().Any(m => m.Id == projectileId && m.Status == MissileFlightStatus.Impact);
    }

    public IReadOnlyList<MissileImpact> TakeImpacts(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var rows = world
            .Set<WorldMissile>()
            .Where(m => m.PilotId == pilotId && m.Outcome != null && !m.Reported)
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
        using WorldDbContext world = new(database.Path);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        world
            .Set<WorldMissile>()
            .Where(m => m.PilotId == pilotId && m.SourceShipId == shipId && m.Status == MissileFlightStatus.Flying)
            .ExecuteUpdate(set => set.SetProperty(m => m.Status, MissileFlightStatus.Expired).SetProperty(m => m.EndedAt, now));
    }
}
