using System.Collections.ObjectModel;
using System.Globalization;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat.Weapons;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

internal sealed record NpcGuardProfile(
    int TypeId,
    string Name,
    int GroupId,
    int CorporationId,
    double Mass,
    double Radius,
    IReadOnlyDictionary<DogmaAttributeId, double> Attributes
);

[Injectable(InjectionType.Singleton)]
internal sealed class NpcGuardSeeder
{
    private readonly Dictionary<int, NpcGuardProfile?> _profiles = [];
    private readonly HashSet<string> _diagnostics = [];

    public NpcGuardSeeder() { }

    internal NpcGuardProfile? Cached(int typeId) => _profiles.GetValueOrDefault(typeId);

    private void Report(string message)
    {
        if (_diagnostics.Add(message))
        {
            ServerLog.Console.Warning("NPC guards: " + message);
        }
    }

    private NpcGuardProfile? Profile(CatalogDbContext catalog, int typeId, int factionId)
    {
        if (_profiles.TryGetValue(typeId, out var cached))
        {
            return cached;
        }
        var type = catalog.Set<ItemType>().Find(typeId);
        var faction = catalog.Set<SourceRecord>().Find("client/factions", Key(factionId))?.Read<ClientFactions>();
        int corporation = checked((int)(faction?.CorporationID ?? 0));
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        var definition = catalog.Set<SourceRecord>().Find("client/types", Key(typeId))?.Read<ClientTypes>();
        if (
            type?.Mass is not > 0
            || type.Radius is not > 0
            || !double.IsFinite(type.Mass.Value)
            || !double.IsFinite(type.Radius.Value)
            || definition is null
            || corporation <= 0
            || !catalog.Set<NpcCorporation>().Any(c => c.Id == corporation)
            || attributes.Values.Any(a => !double.IsFinite(a))
            || attributes.GetValueOrDefault(DogmaAttributeId.StructureHitPoints) <= 0
            || attributes.GetValueOrDefault(DogmaAttributeId.EntityAttackRange) <= 0
            || attributes.GetValueOrDefault(DogmaAttributeId.TrackingSpeed) <= 0
            || attributes.GetValueOrDefault(DogmaAttributeId.WeaponSignatureResolution) <= 0
            || attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity) < 0
            || attributes.GetValueOrDefault(DogmaAttributeId.ArmorHitPoints) < 0
            || !NpcPopulationRules.HasDirectWeapon(attributes)
        )
        {
            Report($"type {typeId} has no valid catalog guard profile; no substitute was used.");
            _profiles[typeId] = null;
            return null;
        }
        try
        {
            _ = NpcMotionRules.SpeedFraction(attributes);
            if (attributes.GetValueOrDefault(DogmaAttributeId.EntityMissileType) is > 0 and var chargeValue)
            {
                int chargeId = checked((int)chargeValue);
                var charge = PilotFittingStore.ReadAttributes(catalog, chargeId);
                _ = NpcMissileRules.Drive(MissileChargeCatalog.Drive(catalog, chargeId, charge), attributes);
                _ = NpcMissileRules.Warhead(charge, attributes);
                if (attributes.GetValueOrDefault(DogmaAttributeId.EntityMissileCycle) <= 0)
                {
                    throw new InvalidDataException("Missing missile cycle.");
                }
            }
        }
        catch (InvalidDataException error)
        {
            Report($"type {typeId}: {error.Message}");
            _profiles[typeId] = null;
            return null;
        }
        if (
            definition.WreckTypeID is long wreckType
            && !catalog.Set<ItemType>().Any(t => t.Id == wreckType && t.GroupId == Eden.Gameplay.Loot.WreckDefinitions.Group)
        )
        {
            Report($"type {typeId} has an invalid wreck link.");
            _profiles[typeId] = null;
            return null;
        }
        var result = new NpcGuardProfile(
            typeId,
            type.Name,
            type.GroupId,
            corporation,
            type.Mass.Value,
            type.Radius.Value,
            new ReadOnlyDictionary<DogmaAttributeId, double>(attributes.ToDictionary())
        );
        _profiles[typeId] = result;
        return result;
    }

    internal void Seed(WorldDbContext world, CatalogDbContext catalog, int systemId, ClientSystems system, NpcSecurityBand band, long now)
    {
        if (band == NpcSecurityBand.Null || system.FactionID is not long faction || NpcGuardRules.Roster(checked((int)faction)) is null)
        {
            return;
        }
        var obstacles = (
            from celestial in catalog.Set<Celestial>()
            join type in catalog.Set<ItemType>() on celestial.TypeId equals type.Id
            where celestial.SolarSystemId == systemId && celestial.GroupId != AsteroidDefinitions.BeltGroup
            select new NpcGuardObstacle(new(celestial.X, celestial.Y, celestial.Z), celestial.Radius ?? type.Radius ?? 0)
        ).ToList();
        var stations = catalog.Set<Station>().Include(s => s.Type).Where(s => s.SolarSystemId == systemId).OrderBy(s => s.Id).ToArray();
        obstacles.AddRange(stations.Select(s => new NpcGuardObstacle(new(s.X, s.Y, s.Z), s.Type.Radius ?? 0)));
        foreach (var gate in catalog.Set<Stargate>().Where(g => g.SolarSystemId == systemId).OrderBy(g => g.Id).ToArray())
        {
            var body = catalog.Set<Celestial>().Find(gate.Id);
            if (body is null)
            {
                Report($"gate {gate.Id} has no catalog position.");
                continue;
            }
            double radius = body.Radius ?? catalog.Set<ItemType>().Find(body.TypeId)?.Radius ?? 0;
            SeedAnchor(
                world,
                catalog,
                gate.Id,
                systemId,
                NpcAnchorKind.Gate,
                checked((int)faction),
                new(body.X, body.Y, body.Z),
                radius,
                [],
                obstacles,
                now
            );
        }
        foreach (var station in stations)
        {
            var owner = catalog.Set<SourceRecord>().Find("client/npccorporations", Key(station.OwnerId))?.Read<ClientNpccorporations>();
            long? ownerFaction =
                owner?.FactionID
                ?? catalog.Set<SourceRecord>().Find("sde/npcCorporations", Key(station.OwnerId))?.Read<SdeNpcCorporations>().FactionID;
            if (ownerFaction is long empire && NpcGuardRules.Roster(checked((int)empire)) is not null)
            {
                SeedAnchor(
                    world,
                    catalog,
                    station.Id,
                    systemId,
                    NpcAnchorKind.Station,
                    checked((int)empire),
                    new(station.X, station.Y, station.Z),
                    station.Type.Radius ?? 0,
                    StationUndockCatalog.Read(catalog, station.TypeId),
                    obstacles,
                    now
                );
            }
        }
    }

    private void SeedAnchor(
        WorldDbContext world,
        CatalogDbContext catalog,
        int anchorId,
        int systemId,
        NpcAnchorKind kind,
        int factionId,
        SpacePosition anchor,
        double radius,
        IReadOnlyList<StationUndockPoint> lanes,
        IReadOnlyList<NpcGuardObstacle> obstacles,
        long now
    )
    {
        var roster = NpcGuardRules.Roster(factionId)!;
        NpcGuardType[] types =
            kind == NpcAnchorKind.Gate ? [roster.Cruiser, roster.Frigate, roster.Customs] : [roster.Cruiser, roster.Frigate];
        var members = types.Select(type => Profile(catalog, (int)type, factionId)).ToArray();
        if (members.Any(m => m is null))
        {
            return;
        }
        var encounter = world.Set<WorldNpcEncounter>().Find((long)anchorId);
        if (encounter is null)
        {
            var home = NpcGuardRules.Home(anchor, radius, lanes, obstacles);
            if (home is null)
            {
                Report($"anchor {anchorId} has no placement clear of collision boundaries and exit lanes.");
                return;
            }
            encounter = new()
            {
                Id = anchorId,
                AnchorId = anchorId,
                SystemId = systemId,
                AnchorKind = kind,
                Role = NpcRole.Navy,
                FactionId = factionId,
                Occupied = true,
                Version = NpcSpawnRules.PopulationVersion,
                HomeX = home.X,
                HomeY = home.Y,
                HomeZ = home.Z,
            };
            world.Add(encounter);
        }
        for (int slot = 0; slot < members.Length; slot++)
        {
            var previous = world
                .Set<WorldNpc>()
                .Where(n => n.EncounterId == encounter.Id && n.SpawnSlot == slot)
                .OrderByDescending(n => n.Id)
                .FirstOrDefault();
            if (
                previous is not null
                && (
                    !previous.DestroyedAt.HasValue
                    || now - previous.DestroyedAt.Value < NpcEncounterDefinitions.RespawnSeconds * TimeSpan.TicksPerSecond
                )
            )
            {
                continue;
            }
            var member = members[slot]!;
            double z = encounter.HomeZ + slot * NpcSpawnRules.MemberSpacing;
            world.Add(
                new WorldNpc
                {
                    EncounterId = encounter.Id,
                    SpawnSlot = slot,
                    AnchorKind = kind,
                    SystemId = systemId,
                    TypeId = member.TypeId,
                    Role = slot == 2 ? NpcRole.Customs : NpcRole.Navy,
                    FactionId = factionId,
                    CorporationId = member.CorporationId,
                    HomeX = encounter.HomeX,
                    HomeY = encounter.HomeY,
                    HomeZ = z,
                    X = encounter.HomeX,
                    Y = encounter.HomeY,
                    Z = z,
                    ShieldCharge = member.Attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity),
                    UpdatedAt = now,
                }
            );
        }
    }

    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);
}
