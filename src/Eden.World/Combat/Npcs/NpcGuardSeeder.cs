using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Combat.Npcs;
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
using Eden.World.Runtime.Economy;
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
    private static readonly FrozenDictionary<NpcEmpireFaction, CatalogDefinition<NpcGuardProfile?>> s_profiles =
        Enum.GetValues<NpcEmpireFaction>()
            .ToFrozenDictionary(
                faction => faction,
                faction => new CatalogDefinition<NpcGuardProfile?>(
                    "npc-guard/" + faction,
                    (catalog, typeId) => LoadProfile(catalog, typeId, (int)faction),
                    8192
                )
            );
    private readonly HashSet<string> _diagnostics = [];

    public NpcGuardSeeder() { }

    private void Report(string message)
    {
        if (_diagnostics.Add(message))
        {
            ServerLog.Console.Warning("NPC guards: " + message);
        }
    }

    private static NpcGuardProfile? Profile(CatalogDbContext catalog, int typeId, int factionId) =>
        catalog.Definition(s_profiles[(NpcEmpireFaction)factionId], typeId);

    private static void ReportDefinition(string message) => ServerLog.Console.Warning("NPC guards: " + message);

    private static NpcGuardProfile? LoadProfile(CatalogDbContext catalog, int typeId, int factionId)
    {
        var type = catalog.FittingType(typeId);
        var faction = catalog.Read(CatalogData.Client.Factions, (long)factionId);
        int corporation = checked((int)(faction?.CorporationID ?? 0));
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        var definition = catalog.Read(CatalogData.Client.Types, (long)typeId);
        if (
            type?.Mass is not > 0
            || type.Radius is not > 0
            || !double.IsFinite(type.Mass.Value)
            || !double.IsFinite(type.Radius.Value)
            || definition is null
            || corporation <= 0
            || catalog.NpcCorporationName(corporation) is null
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
            ReportDefinition($"type {typeId} has no valid catalog guard profile; no substitute was used.");
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
            ReportDefinition($"type {typeId}: {error.Message}");
            return null;
        }
        if (
            definition.WreckTypeID is long wreckType
            && catalog.FittingType(wreckType)?.GroupId != Eden.Gameplay.Loot.WreckDefinitions.Group
        )
        {
            ReportDefinition($"type {typeId} has an invalid wreck link.");
            return null;
        }
        var result = new NpcGuardProfile(
            typeId,
            type.Name,
            type.GroupId,
            corporation,
            type.Mass.Value,
            type.Radius.Value,
            attributes.ToImmutableDictionary()
        );
        return result;
    }

    internal static List<NpcGuardObstacle> CatalogObstacles(CatalogDbContext catalog, int systemId)
    {
        List<NpcGuardObstacle> obstacles = [];
        var bodies = catalog.CelestialsInSystem(systemId).Where(body => body.GroupId != AsteroidDefinitions.BeltGroup).ToArray();
        foreach (var body in bodies)
        {
            if (catalog.FittingType(body.TypeId) is { } type)
            {
                obstacles.Add(new(new(body.X, body.Y, body.Z), body.Radius ?? type.Radius ?? 0));
            }
        }
        return obstacles;
    }

    internal void Seed(WorldAction world, CatalogDbContext catalog, int systemId, ClientSystems system, NpcSecurityBand band, long now)
    {
        if (band == NpcSecurityBand.Null || system.FactionID is not long faction || NpcGuardRules.Roster(checked((int)faction)) is null)
        {
            return;
        }
        var obstacles = NpcGuardSeeder.CatalogObstacles(catalog, systemId);
        var stations = catalog.StationsInSystem(systemId).Where(station => catalog.FittingType(station.TypeId) is not null).ToArray();
        obstacles.AddRange(stations.Select(s => new NpcGuardObstacle(new(s.X, s.Y, s.Z), catalog.FittingType(s.TypeId)?.Radius ?? 0)));
        foreach (var gate in catalog.GatesInSystem(systemId))
        {
            var body = catalog.FindCelestial(gate.Id);
            if (body is null)
            {
                Report($"gate {gate.Id} has no catalog position.");
                continue;
            }
            double radius = body.Radius ?? catalog.FittingType(body.TypeId)?.Radius ?? 0;
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
            var owner = catalog.Read(CatalogData.Client.Npccorporations, (long)station.OwnerId);
            long? ownerFaction = owner?.FactionID ?? catalog.Read(CatalogData.Sde.NpcCorporations, (long)station.OwnerId)?.FactionID;
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
                    catalog.FittingType(station.TypeId)?.Radius ?? 0,
                    StationUndockCatalog.Read(catalog, station.TypeId),
                    obstacles,
                    now
                );
            }
        }
    }

    private void SeedAnchor(
        WorldAction world,
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
                .Filter<WorldNpc>(n => n.EncounterId == encounter.Id && n.SpawnSlot == slot)
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
}
