using System.Collections.Concurrent;
using Eden.Data.Catalog.Exploration;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Exploration;

internal sealed partial class CombatAnomalyStore
{
    private sealed record DedView(
        long Version,
        long Catalog,
        long Deadline,
        bool Pirates,
        DedComplex? Profile,
        IReadOnlyList<CombatAnomaly> Sites
    );

    private readonly ConcurrentDictionary<int, DedView> _dedViews = [];

    private IReadOnlyList<CombatAnomaly> DedSignatures(int system, long now)
    {
        long version = Version(system);
        long generation = database.Catalog.Identity.Generation;
        bool pirates = database.Settings.NpcPopulation.Pirates;
        if (
            _dedViews.TryGetValue(system, out var cached)
            && cached.Version == version
            && cached.Catalog == generation
            && cached.Pirates == pirates
            && now < cached.Deadline
        )
        {
            return cached.Sites;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var definitions = DedComplexCatalog.Read(catalog);
        var summary = definitions.IsEmpty ? null : catalog.FindSystem(system);
        var faction = summary is null ? null : NpcRegions.Pirates(summary.RegionId);
        var security = faction is null ? null : catalog.Read(CatalogData.Client.Systems, (long)system)?.SecurityStatus;
        var definition = security is > 0 ? definitions.FirstOrDefault(d => d.FactionId == (int?)faction) : null;
        if (!pirates || definition is null)
        {
            _dedViews[system] = new(version, generation, long.MaxValue, pirates, null, []);
            return _dedViews[system].Sites;
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var site = world
            .Set<WorldCombatAnomaly>()
            .Where(s => s.SystemId == system && s.Slot == DedComplexRules.SiteSlot)
            .OrderByDescending(s => s.Id)
            .FirstOrDefault();
        if (site is null || site.ReplaceAt <= now)
        {
            var anchor = catalog.CelestialsInSystem(system).OrderBy(c => c.Id).FirstOrDefault();
            if (anchor is null)
            {
                _dedViews[system] = new(version, generation, long.MaxValue, pirates, null, []);
                return _dedViews[system].Sites;
            }
            long id = Math.Max(
                CombatAnomalyRules.FirstSiteId,
                (world.Set<WorldCombatAnomaly>().Max(s => (long?)s.Id) ?? CombatAnomalyRules.FirstSiteId - 1) + 1
            );
            const int placementSlot = 20;
            double offset = placementSlot * CombatAnomalyRules.PlacementOffset;
            site = new()
            {
                Id = id,
                DungeonId = definition.Binding.DungeonId,
                SystemId = system,
                IsSignature = true,
                Slot = DedComplexRules.SiteSlot,
                X = anchor.X + offset,
                Y = anchor.Y + offset,
                Z = anchor.Z - offset,
            };
            world.Add(site);
            world.SaveChanges();
            Invalidate(world, -1);
        }
        transaction.Commit();
        IReadOnlyList<CombatAnomaly> contacts = site.CompletedAt is null ? [DedContact(site, definition)] : [];
        _dedViews[system] = new(version, generation, site.ReplaceAt ?? long.MaxValue, pirates, definition, contacts);
        return contacts;
    }

    private static SpacePosition DedOrigin(WorldCombatAnomaly site) => new(site.X, site.Y, site.Z);

    private static long DedGateId(long site, int room) =>
        DedComplexRules.FirstGateId + (site - CombatAnomalyRules.FirstSiteId) * DedComplexRules.GatesPerSite + room;

    private static CombatAnomaly DedContact(WorldCombatAnomaly site, DedComplex profile)
    {
        var origin = DedOrigin(site);
        var objects = profile
            .Rooms.SelectMany(
                (room, index) =>
                    room.Objects.Select(body =>
                        body with
                        {
                            Id =
                                body.Id == room.Gate?.ObjectId
                                    ? DedGateId(site.Id, index)
                                    : CombatAnomalyRules.FirstObjectId
                                        + (site.Id - CombatAnomalyRules.FirstSiteId) * AnomalyEncounterRules.ObjectsPerSite
                                        + profile.Rooms.Take(index).Sum(r => r.Objects.Length)
                                        + room.Objects.IndexOf(body),
                            Position = origin + body.Position,
                        }
                    )
            )
            .ToArray();
        return new(
            site.Id,
            ScannerTargetIds.FromCombatSequence(site.Id - CombatAnomalyRules.FirstSiteId),
            site.SystemId,
            site.DungeonId,
            profile.NameId,
            profile.Difficulty,
            origin,
            site.Wave >= 0,
            objects,
            profile.FactionId,
            IsSignature: true,
            Ded: true
        );
    }

    private void ArriveDed(int system, SpacePosition position, long now)
    {
        var contact = DedSignatures(system, now).SingleOrDefault();
        if (contact is null)
        {
            return;
        }
        var profile = _dedViews[system].Profile!;
        int index = -1;
        for (int room = 0; room < profile.Rooms.Length; room++)
        {
            if (position.DistanceTo(contact.Position + profile.Rooms[room].Offset) <= CombatAnomalyRules.ArrivalRange)
            {
                index = room;
                break;
            }
        }
        if (index < 0 || _dedStarted.TryGetValue(system, out var started) && started.Site == contact.Id && index <= started.Room)
        {
            return;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var site = world.Set<WorldCombatAnomaly>().Find(contact.Id)!;
        if (site.CompletedAt is null && index == site.Wave + 1)
        {
            SpawnDedRoom(world, catalog, site, profile, index, now);
            site.Wave = index;
            world.SaveChanges();
        }
        transaction.Commit();
        _dedStarted[system] = (site.Id, site.Wave);
        if (site.Wave == index)
        {
            Invalidate(world, system);
        }
    }

    private readonly ConcurrentDictionary<int, (long Site, int Room)> _dedStarted = [];

    private static void SpawnDedRoom(
        WorldDbContext world,
        CatalogDbContext catalog,
        WorldCombatAnomaly site,
        DedComplex profile,
        int index,
        long now
    )
    {
        int slot = 0;
        foreach (var member in profile.Rooms[index].Members)
        {
            var binding = member.Binding;
            var hull = new NpcProfileCatalog().Read(catalog, binding.TypeId);
            int faction = binding.FactionId == 0 ? profile.FactionId : binding.FactionId;
            int corporation =
                faction == profile.FactionId
                    ? profile.CorporationId
                    : checked((int)catalog.Require(CatalogData.Client.Factions, (long)faction).CorporationID!.Value);
            for (int i = 0; i < binding.Count; i++)
            {
                var point = DedOrigin(site) + member.Position + new SpacePosition(i * NpcSpawnRules.MemberSpacing, 0, 0);
                long id =
                    CombatAnomalyRules.FirstNpcId
                    + (site.Id - CombatAnomalyRules.FirstSiteId) * AnomalyEncounterRules.NpcsPerSite
                    + index * AnomalyEncounterRules.MembersPerWave
                    + slot;
                world.Add(
                    new WorldNpc
                    {
                        Id = id,
                        SystemId = site.SystemId,
                        TypeId = binding.TypeId,
                        Name = binding.Name,
                        FactionId = faction,
                        CorporationId = corporation,
                        Role = binding.Structure ? NpcRole.MissionStructure : NpcRole.Pirate,
                        AnchorKind = NpcAnchorKind.Anomaly,
                        EncounterId = site.Id,
                        SpawnSlot = slot++,
                        AcquisitionRange = member.AcquisitionRange,
                        X = point.X,
                        Y = point.Y,
                        Z = point.Z,
                        HomeX = point.X,
                        HomeY = point.Y,
                        HomeZ = point.Z,
                        ShieldCharge = hull.Defense.Shield,
                        UpdatedAt = now,
                        MotionAt = now,
                    }
                );
                world.Add(
                    new WorldAnomalyMember
                    {
                        NpcId = id,
                        SiteId = site.Id,
                        Wave = index,
                    }
                );
            }
        }
    }

    public MissionGate Gate(long pilotId, long gateId, long now)
    {
        return LiveWorld.Execute(
            database.Path,
            () =>
            {
                using var ownership = LiveWorld.Find(database.Path)?.Enter();
                using var movement = database.EnterMovement();
                using WorldDbContext world = new(database.Path);
                using CatalogDbContext catalog = new(database.CatalogPath);
                return ValidateDedGate(world, catalog, pilotId, gateId).Gate;
            },
            nameof(Gate)
        );
    }

    public MissionGateActivation ActivateGate(long pilotId, long gateId, SpacePosition destination, long now) =>
        LiveWorld.Execute(
            database.Path,
            () =>
            {
                using var ownership = LiveWorld.Find(database.Path)?.Enter();
                using var movement = database.EnterMovement();
                using WorldDbContext world = new(database.Path);
                using var transaction = world.Database.BeginTransaction();
                using CatalogDbContext catalog = new(database.CatalogPath);
                var (site, pilot, gate) = ValidateDedGate(world, catalog, pilotId, gateId);
                if (gate.Destination != destination)
                {
                    throw new InvalidDataException("The acceleration gate destination changed.");
                }
                var key = gate.Access.Key;
                var unlock = key is { Consume: true } ? world.Set<SavedDedGateUnlock>().Find(site.Id, gate.DungeonObjectId, pilotId) : null;
                var result = MissionGatePayment.Pay(world, catalog, pilotId, pilot.ActiveShipId, key, unlock?.ResetAt > now);
                if (key is { Consume: true } && !(unlock?.ResetAt > now))
                {
                    unlock ??= world
                        .Add(
                            new SavedDedGateUnlock
                            {
                                SiteId = site.Id,
                                GateObjectId = gate.DungeonObjectId,
                                PilotId = pilotId,
                            }
                        )
                        .Entity;
                    unlock.ResetAt = MissionGateAccessRules.NextDowntime(DateTimeOffset.FromFileTime(now)).UtcDateTime.ToFileTimeUtc();
                }
                world.SaveChanges();
                transaction.Commit();
                return result;
            },
            nameof(ActivateGate)
        );

    private static (WorldCombatAnomaly Site, Pilot Pilot, MissionGate Gate) ValidateDedGate(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        long gateId
    )
    {
        if (!DedComplexRules.IsGate(gateId))
        {
            throw new InvalidDataException("The DED acceleration gate is unavailable.");
        }
        long sequence = gateId - DedComplexRules.FirstGateId;
        long id = CombatAnomalyRules.FirstSiteId + sequence / DedComplexRules.GatesPerSite;
        int room = (int)(sequence % DedComplexRules.GatesPerSite);
        var site = world.Set<WorldCombatAnomaly>().Find(id);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var ship = pilot.ActiveShip;
        if (
            site is not { Slot: DedComplexRules.SiteSlot, CompletedAt: null }
            || site.Wave < room
            || ship.Position is not { } position
            || (position.SystemId ?? catalog.RequireStation(ship.StationId).SolarSystemId) != site.SystemId
        )
        {
            throw new InvalidDataException("The DED acceleration gate is unavailable in your current room.");
        }
        var profile = DedComplexCatalog.Find(catalog, site.DungeonId);
        if (room >= profile.Rooms.Length - 1 || profile.Rooms[room].Gate is not { } source)
        {
            throw new InvalidDataException("The DED acceleration gate is unavailable.");
        }
        var origin = DedOrigin(site);
        MissionGate gate = new(
            gateId,
            origin + source.Body.Position,
            source.Body.Radius,
            origin + profile.Rooms[room + 1].Offset,
            source.RequiresClear
        )
        {
            Access = source.Access,
            DungeonObjectId = source.ObjectId,
        };
        var type = catalog.RequireItem(ship.TypeId);
        var item = new InventoryItem(
            ship.Id,
            type.Id,
            pilot.Id,
            ship.StationId,
            (int)InventoryFlag.Hangar,
            1,
            type.GroupId,
            type.CategoryId,
            0,
            true
        );
        int race = checked((int)(catalog.Read(CatalogData.Client.Types, (long)type.Id)?.RaceID ?? 0));
        var fitted = source.Access.ForbiddenEquipment is null
            ? []
            : world
                .Set<OwnedItem>()
                .Where(i => i.LocationId == ship.Id && i.OwnerId == pilot.Id)
                .ToArray()
                .Where(i =>
                    InventoryDefinitions.IsFittedSlot(i.FlagId)
                    || i.FlagId is >= (int)InventoryFlag.SubsystemSlot0 and <= (int)InventoryFlag.SubsystemSlot3
                )
                .Select(i => PilotInventoryStore.Read(catalog, i))
                .ToArray();
        MissionGateAccessRules.ValidateShip(source.Access, item, race, fitted);
        bool cleared =
            !source.RequiresClear
            || !world
                .Set<WorldAnomalyMember>()
                .Any(m =>
                    m.SiteId == site.Id
                    && m.Wave == room
                    && world.Set<WorldNpc>().Any(n => n.Id == m.NpcId && n.DestroyedAt == null && n.RetiredAt == null)
                );
        MissionGateRules.Validate(new(position.X, position.Y, position.Z), type.Radius ?? 0, type.GroupId, gate, cleared);
        return (site, pilot, gate);
    }

    public SpacePosition WarpDestination(int systemId, SpacePosition origin, SpacePosition destination, long now)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using var movement = database.EnterMovement();
        var contact = DedSignatures(systemId, now).SingleOrDefault();
        if (contact is null)
        {
            return destination;
        }
        var profile = _dedViews[systemId].Profile!;
        if (
            !profile
                .Rooms.Skip(1)
                .Any(r => (contact.Position + r.Offset).DistanceTo(destination) <= NpcEncounterDefinitions.VisibilityRange)
        )
        {
            return destination;
        }
        if (profile.Rooms.Any(r => (contact.Position + r.Offset).DistanceTo(origin) <= NpcEncounterDefinitions.VisibilityRange))
        {
            throw new InvalidDataException("Use an acceleration gate to travel inside this deadspace complex.");
        }
        return contact.Position;
    }

    private static bool DedKilled(WorldAction world, CatalogDbContext catalog, WorldCombatAnomaly site, WorldNpc npc, long now)
    {
        var profile = DedComplexCatalog.Find(catalog, site.DungeonId);
        if (npc.Role != NpcRole.MissionStructure || npc.TypeId != profile.Binding.RewardStructureType)
        {
            return false;
        }
        Complete(site, now);
        foreach (
            var member in world
                .Set<WorldNpc>()
                .Where(n => n.EncounterId == site.Id && n.Id != npc.Id && n.DestroyedAt == null && n.RetiredAt == null)
                .ToArray()
        )
        {
            member.RetiredAt = now;
        }
        return true;
    }

    internal static IReadOnlyList<DedLootSource>? DedLoot(WorldAction world, CatalogDbContext catalog, WorldNpc npc)
    {
        if (
            npc.AnchorKind != NpcAnchorKind.Anomaly
            || npc.Role != NpcRole.MissionStructure && !DedComplexCatalog.IsOverseer(npc.TypeId)
            || world.Set<WorldCombatAnomaly>().Find(npc.EncounterId) is not { Slot: DedComplexRules.SiteSlot } site
        )
        {
            return null;
        }
        var binding = DedComplexCatalog.Find(catalog, site.DungeonId).Binding;
        if (npc.TypeId == binding.OverseerType)
        {
            return binding.OverseerLoot;
        }
        if (npc.TypeId != binding.RewardStructureType)
        {
            return null;
        }
        // Exact retail rates are server-side. A stable kill identity selects Eden's loot.
        Random random = new(unchecked((int)NpcSpawnRules.Seed(npc.SystemId, npc.Id)));
        List<DedLootSource> loot = [.. binding.GuaranteedLoot, new(binding.DeadspaceModules[random.Next(binding.DeadspaceModules.Count)])];
        if (binding.FactionModules.Count > 0 && random.Next(5) == 0)
        {
            loot.Add(new(binding.FactionModules[random.Next(binding.FactionModules.Count)]));
        }
        if (random.Next(20) == 0)
        {
            loot.Add(new(binding.Blueprint, BlueprintCopy: true));
        }
        return loot;
    }
}
