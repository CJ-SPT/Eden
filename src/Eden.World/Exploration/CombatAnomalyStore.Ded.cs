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
using Eden.World.Skills;

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
        var security =
            summary is null || WormholeRules.IsWormholeSystem(system)
                ? null
                : catalog.Read(CatalogData.Client.Systems, (long)system)?.SecurityStatus;
        var eligible = security is double status
            ? definitions
                .Where(d =>
                    (d.Binding.SecurityBands & DedComplexRules.SecurityBand(status)) != 0
                    && (d.FactionId == (int?)faction || d.FactionId == (int)NpcPirateFaction.RogueDrones)
                )
                .ToArray()
            : [];
        if (!pirates || eligible.Length == 0)
        {
            _dedViews[system] = new(version, generation, long.MaxValue, pirates, null, []);
            return _dedViews[system].Sites;
        }
        using WorldActionScope transaction = new(database, new(systems: [system]));
        var world = transaction.World;
        var site = world
            .Filter<WorldCombatAnomaly>(s => s.SystemId == system && s.Slot == DedComplexRules.SiteSlot)
            .OrderByDescending(s => s.Id)
            .FirstOrDefault();
        var definition = site is null ? null : definitions.FirstOrDefault(d => d.Binding.DungeonId == site.DungeonId);
        if (site is null || site.ReplaceAt <= now || definition is null || !eligible.Contains(definition))
        {
            long? previousId = site?.Id;
            var anchor = catalog.CelestialsInSystem(system).OrderBy(c => c.Id).FirstOrDefault();
            if (anchor is null)
            {
                _dedViews[system] = new(version, generation, long.MaxValue, pirates, null, []);
                return _dedViews[system].Sites;
            }
            int previous = Array.IndexOf(eligible, definition);
            int next =
                previous < 0
                    ? (int)(NpcSpawnRules.Seed(system, DedComplexRules.SiteSlot) % eligible.Length)
                    : (previous + 1) % eligible.Length;
            definition = eligible[next];
            const int placementSlot = 20;
            double offset = placementSlot * CombatAnomalyRules.PlacementOffset;
            site = new()
            {
                Id = NextSiteId(world),
                DungeonId = definition.Binding.DungeonId,
                SystemId = system,
                IsSignature = true,
                Slot = DedComplexRules.SiteSlot,
                X = anchor.X + offset,
                Y = anchor.Y + offset,
                Z = anchor.Z - offset,
            };
            world.Add(site);
            if (previousId is long replaced)
            {
                world.AfterCommit(() => _dedStarted.TryRemove(replaced, out _));
            }
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
                            Id = room.Gates.FirstOrDefault(g => g.Body.Id == body.Id) is { } gate
                                ? DedGateId(site.Id, gate.Slot)
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

    private void ArriveDed(CombatAnomaly contact, DedComplex profile, SpacePosition position, long now)
    {
        int index = -1;
        for (int room = 0; room < profile.Rooms.Length; room++)
        {
            if (position.DistanceTo(contact.Position + profile.Rooms[room].Offset) <= CombatAnomalyRules.ArrivalRange)
            {
                index = room;
                break;
            }
        }
        if (index < 0 || _dedStarted.TryGetValue(contact.Id, out long started) && (started & (1L << index)) != 0)
        {
            return;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        using WorldActionScope transaction = new(database, new(systems: [contact.SystemId]));
        var world = transaction.World;
        var site = world.Set<WorldCombatAnomaly>().Find(contact.Id)!;
        long visited = Visited(site);
        if (site.CompletedAt is null && (visited & (1L << index)) == 0)
        {
            SpawnDedRoom(world, catalog, site, profile, index, now);
            site.Wave = Math.Max(site.Wave, index);
            site.DedVisitedRooms = visited | (1L << index);
            Invalidate(world, site.SystemId);
        }
        transaction.Commit();
        _dedStarted[contact.Id] = Visited(site);
    }

    private readonly ConcurrentDictionary<long, long> _dedStarted = [];

    private static long Visited(WorldCombatAnomaly site) =>
        site.DedVisitedRooms == 0 && site.Wave >= 0 ? (1L << (site.Wave + 1)) - 1 : site.DedVisitedRooms;

    private static long NextSiteId(WorldAction world)
    {
        long saved =
            world.Filter<WorldCombatAnomaly>(s => s.Id >= CombatAnomalyRules.FirstSiteId).Max(s => (long?)s.Id)
            ?? CombatAnomalyRules.FirstSiteId - 1;
        long staged = world.Set<WorldCombatAnomaly>().Local.Select(s => s.Id).DefaultIfEmpty().Max();
        return Math.Max(saved, staged) + 1;
    }

    private static void SpawnDedRoom(
        WorldAction world,
        CatalogDbContext catalog,
        WorldCombatAnomaly site,
        DedComplex profile,
        int index,
        long now,
        int stage = 0
    )
    {
        int slot = 0;
        foreach (var member in profile.Rooms[index].Members)
        {
            var binding = member.Binding;
            if (binding.Stage != stage)
            {
                slot += binding.Count;
                continue;
            }
            var hull = new NpcProfileCatalog().Read(catalog, binding.TypeId);
            bool sentry = hull.Type.GroupId is NpcEncounterDefinitions.SentryGroup or NpcEncounterDefinitions.OverseerSentryGroup;
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
                        Role = binding.Structure && !sentry ? NpcRole.MissionStructure : NpcRole.Pirate,
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
                        Sentry = sentry,
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
                using CatalogDbContext catalog = new(database.CatalogPath);
                return WorldActionScope.Inspect(database, world => ValidateDedGate(world, catalog, pilotId, gateId, now).Gate);
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
                using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
                var world = transaction.World;
                using CatalogDbContext catalog = new(database.CatalogPath);
                var (site, pilot, gate) = ValidateDedGate(world, catalog, pilotId, gateId, now);
                if (gate.Destination != destination)
                {
                    throw new InvalidDataException("The acceleration gate destination changed.");
                }
                var key = gate.Access.Key;
                var unlock = key is { Consume: true }
                    ? world
                        .Filter<SavedDedGateUnlock>(g =>
                            g.SiteId == site.Id && g.GateObjectId == gate.DungeonObjectId && g.PilotId == pilotId
                        )
                        .SingleOrDefault()
                    : null;
                var result = MissionGatePayment.Pay(world, catalog, pilotId, pilot.ActiveShipId, key, unlock?.ResetAt > now);
                if (result.Items.Count != 0)
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
                transaction.Commit();
                return result;
            },
            nameof(ActivateGate)
        );

    private static (WorldCombatAnomaly Site, Pilot Pilot, MissionGate Gate) ValidateDedGate(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long gateId,
        long now
    )
    {
        if (!DedComplexRules.IsGate(gateId))
        {
            throw new InvalidDataException("The DED acceleration gate is unavailable.");
        }
        long sequence = gateId - DedComplexRules.FirstGateId;
        long id = CombatAnomalyRules.FirstSiteId + sequence / DedComplexRules.GatesPerSite;
        int slot = (int)(sequence % DedComplexRules.GatesPerSite);
        var site = world.Set<WorldCombatAnomaly>().Find(id);
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown DED pilot.");
        var ship = pilot.ActiveShip;
        if (
            site is null
            || !DedComplexRules.IsSite(site.Slot)
            || site.CompletedAt is not null
            || site.EscalationPilotId is long owner && (owner != pilotId || site.EscalationExpiresAt <= now)
            || ship.Position is not { } position
            || (position.SystemId ?? catalog.RequireStation(ship.StationId).SolarSystemId) != site.SystemId
        )
        {
            throw new InvalidDataException("The DED acceleration gate is unavailable in your current room.");
        }
        var profile = DedComplexCatalog.Find(catalog, site.DungeonId);
        var gateRoom = profile.Rooms.Select((room, index) => (room, index)).FirstOrDefault(r => r.room.Gates.Any(g => g.Slot == slot));
        int room = gateRoom.index;
        if (gateRoom.room is null || (Visited(site) & (1L << room)) == 0)
        {
            throw new InvalidDataException("The DED acceleration gate is unavailable.");
        }
        var source = profile.Rooms[room].Gates.Single(g => g.Slot == slot);
        if (source.RequiredSkill != 0 && PilotSkillReader.Level(world, catalog, pilotId, source.RequiredSkill) < source.RequiredLevel)
        {
            throw new InvalidDataException(
                $"This acceleration gate requires {catalog.RequireItem(source.RequiredSkill).Name} level {source.RequiredLevel}."
            );
        }
        var origin = DedOrigin(site);
        MissionGate gate = new(
            gateId,
            origin + source.Body.Position,
            source.Body.Radius,
            origin + profile.Rooms[source.Destination].Offset,
            source.RequiresClear
        )
        {
            Access = source.Access,
            DungeonObjectId = source.Body.Id,
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
                .Filter<OwnedItem>(i => i.LocationId == ship.Id && i.OwnerId == pilot.Id)
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
                .Filter<WorldAnomalyMember>(m => m.SiteId == site.Id && m.Wave == room && !m.Sentry)
                .AsEnumerable()
                .Select(member => world.Set<WorldNpc>().Find(member.NpcId))
                .Any(n => n is { Role: NpcRole.Pirate, DestroyedAt: null, RetiredAt: null });
        MissionGateRules.Validate(new(position.X, position.Y, position.Z), type.Radius ?? 0, type.GroupId, gate, cleared);
        return (site, pilot, gate);
    }

    public SpacePosition WarpDestination(int systemId, SpacePosition origin, SpacePosition destination, long now, long pilotId = 0)
    {
        using var ownership = LiveWorld.Find(database.Path)?.Enter();
        using var movement = database.EnterMovement();
        foreach (var contact in DedSignatures(systemId, now))
        {
            if (RedirectDedWarp(contact, _dedViews[systemId].Profile!, origin, destination) is { } entry)
            {
                return entry;
            }
        }
        var privateSites = pilotId == 0 ? [] : Escalations(pilotId, now).Sites;
        if (privateSites.Any(s => s.SystemId == systemId))
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            foreach (var site in privateSites.Where(s => s.SystemId == systemId))
            {
                if (RedirectDedWarp(site, DedComplexCatalog.Find(catalog, site.DungeonId), origin, destination) is { } entry)
                {
                    return entry;
                }
            }
        }
        return destination;
    }

    private static SpacePosition? RedirectDedWarp(
        CombatAnomaly contact,
        DedComplex profile,
        SpacePosition origin,
        SpacePosition destination
    )
    {
        if (
            !profile
                .Rooms.Skip(1)
                .Any(r => (contact.Position + r.Offset).DistanceTo(destination) <= NpcEncounterDefinitions.VisibilityRange)
        )
        {
            return null;
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
        var binding = profile.Binding;
        if (site.EscalationExpiresAt <= now)
        {
            Complete(site, now);
            RetireDedMembers(world, site, now, npc.Id);
            return true;
        }
        var member = world.Set<WorldAnomalyMember>().Find(npc.Id);
        if (member is { Sentry: false } && profile.Rooms[member.Wave].Members.Any(m => m.Binding.Stage != 0))
        {
            AdvanceDedStages(world, catalog, site, npc, profile, member.Wave, now);
        }
        if (npc.TypeId != binding.RewardStructureType)
        {
            if (
                binding.ClearRoomId == 0
                || member is null
                || binding.Rooms[member.Wave].RoomId != binding.ClearRoomId
                || world
                    .Filter<WorldAnomalyMember>(m => m.SiteId == site.Id && m.Wave == member.Wave && m.NpcId != npc.Id && !m.Sentry)
                    .ToArray()
                    .Any(m => world.Set<WorldNpc>().Find(m.NpcId) is { DestroyedAt: null, RetiredAt: null })
            )
            {
                return false;
            }
        }
        Complete(site, now);
        RetireDedMembers(world, site, now, npc.Id);
        return true;
    }

    private static void AdvanceDedStages(
        WorldAction world,
        CatalogDbContext catalog,
        WorldCombatAnomaly site,
        WorldNpc killed,
        DedComplex profile,
        int room,
        long now
    )
    {
        var members = world
            .Filter<WorldAnomalyMember>(m => m.SiteId == site.Id && m.Wave == room && !m.Sentry)
            .AsEnumerable()
            .UnionBy(world.Set<WorldAnomalyMember>().Local.Where(m => m.SiteId == site.Id && m.Wave == room && !m.Sentry), m => m.NpcId)
            .ToArray();
        if (members.Any(m => m.NpcId != killed.Id && world.Set<WorldNpc>().Find(m.NpcId) is { DestroyedAt: null, RetiredAt: null }))
        {
            return;
        }
        int lastSlot = members.Max(m => world.Set<WorldNpc>().Find(m.NpcId)?.SpawnSlot ?? 0);
        int firstSlot = 0;
        foreach (var member in profile.Rooms[room].Members)
        {
            if (firstSlot > lastSlot)
            {
                SpawnDedRoom(world, catalog, site, profile, room, now, member.Binding.Stage);
                return;
            }
            firstSlot += member.Binding.Count;
        }
    }

    private static void RetireDedMembers(WorldAction world, WorldCombatAnomaly site, long now, long killed = 0)
    {
        foreach (
            var member in world
                .Filter<WorldNpc>(n => n.EncounterId == site.Id && n.Id != killed && n.DestroyedAt == null && n.RetiredAt == null)
                .ToArray()
        )
        {
            member.RetiredAt = now;
        }
    }

    internal static IReadOnlyList<DedLootSource>? DedLoot(WorldAction world, CatalogDbContext catalog, WorldNpc npc)
    {
        if (
            npc.AnchorKind != NpcAnchorKind.Anomaly
            || !DedComplexCatalog.HasReward(npc.TypeId)
            || world.Set<WorldCombatAnomaly>().Find(npc.EncounterId) is not { } site
            || !DedComplexRules.IsSite(site.Slot)
        )
        {
            return null;
        }
        var binding = DedComplexCatalog.Find(catalog, site.DungeonId).Binding;
        if (npc.TypeId == binding.OverseerType)
        {
            return binding.OverseerLoot;
        }
        if ((binding.SecondaryRewards ?? []).FirstOrDefault(r => r.TypeId == npc.TypeId) is { } secondary)
        {
            return secondary.Loot;
        }
        if (npc.TypeId != binding.RewardStructureType)
        {
            return null;
        }
        // Exact retail rates are server-side. A stable kill identity selects Eden's loot.
        Random random = new(unchecked((int)NpcSpawnRules.Seed(npc.SystemId, npc.Id)));
        List<DedLootSource> loot = [.. binding.GuaranteedLoot];
        if (binding.DeadspaceModules.Count > 0 && (binding.Revision == 0 || random.Next(2) == 0))
        {
            loot.Add(new(binding.DeadspaceModules[random.Next(binding.DeadspaceModules.Count)]));
        }
        if (binding.FactionModules.Count > 0 && random.Next(5) == 0)
        {
            loot.Add(new(binding.FactionModules[random.Next(binding.FactionModules.Count)]));
        }
        if (binding.Blueprint != 0 && random.Next(20) == 0)
        {
            loot.Add(new(binding.Blueprint, BlueprintCopy: true));
        }
        return loot;
    }
}
