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

    internal static void WarmCatalog(CatalogDbContext catalog)
    {
        foreach (var faction in Enum.GetValues<NpcEmpireFaction>())
        {
            if (NpcGuardRules.Roster((int)faction) is not { } roster)
            {
                continue;
            }
            foreach (var type in new[] { roster.Frigate, roster.Cruiser, roster.Customs }.Distinct())
            {
                _ = Profile(catalog, (int)type, (int)faction);
            }
        }
    }

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
        var attributes = NpcProfileCatalog.ReadAttributes(catalog, typeId);
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

    private static readonly CatalogDefinition<IReadOnlyList<NpcGuardObstacle>> s_obstacles = new(
        "npc-anchor-obstacles",
        LoadCatalogObstacles,
        Estimate: obstacles => 32L + 64L * obstacles.Count
    );

    internal static IReadOnlyList<NpcGuardObstacle> CatalogObstacles(CatalogDbContext catalog, int systemId) =>
        catalog.Definition(s_obstacles, systemId);

    internal static void WarmSystem(CatalogDbContext catalog, int systemId)
    {
        if (catalog.Definition(s_guardAnchors, systemId).Sites.Length != 0)
        {
            _ = CatalogObstacles(catalog, systemId);
        }
    }

    private static IReadOnlyList<NpcGuardObstacle> LoadCatalogObstacles(CatalogDbContext catalog, int systemId)
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
        foreach (var station in catalog.StationsInSystem(systemId))
        {
            if (catalog.FittingType(station.TypeId) is { } type)
            {
                obstacles.Add(new(new(station.X, station.Y, station.Z), type.Radius ?? 0));
            }
        }
        return obstacles.AsReadOnly();
    }

    private sealed record GuardAnchor(
        int Id,
        NpcAnchorKind Kind,
        int FactionId,
        SpacePosition Position,
        double Radius,
        IReadOnlyList<StationUndockPoint> Lanes
    );

    private sealed record GuardAnchors(GuardAnchor[] Sites, int[] MissingGates);

    private static readonly CatalogDefinition<GuardAnchors> s_guardAnchors = new(
        "npc-guard-anchors",
        LoadGuardAnchors,
        Estimate: anchors => 64L + 128L * anchors.Sites.Length
    );

    private static GuardAnchors LoadGuardAnchors(CatalogDbContext catalog, int systemId)
    {
        List<GuardAnchor> sites = [];
        List<int> missing = [];
        if (catalog.SystemPolitics(systemId).FactionId is not long faction || NpcGuardRules.Roster(checked((int)faction)) is null)
        {
            return new([], []);
        }
        foreach (var gate in catalog.GatesInSystem(systemId))
        {
            if (catalog.FindGateBody(gate.Id) is not { } body)
            {
                missing.Add(gate.Id);
                continue;
            }
            double radius = body.Radius ?? catalog.FittingType(body.TypeId)?.Radius ?? 0;
            sites.Add(new(gate.Id, NpcAnchorKind.Gate, checked((int)faction), new(body.X, body.Y, body.Z), radius, []));
        }
        foreach (var station in catalog.StationsInSystem(systemId))
        {
            if (catalog.FittingType(station.TypeId) is not { } type)
            {
                continue;
            }
            var owner = catalog.Read(CatalogData.Client.Npccorporations, (long)station.OwnerId);
            long? ownerFaction = owner?.FactionID ?? catalog.Read(CatalogData.Sde.NpcCorporations, (long)station.OwnerId)?.FactionID;
            if (ownerFaction is long empire && NpcGuardRules.Roster(checked((int)empire)) is not null)
            {
                sites.Add(
                    new(
                        station.Id,
                        NpcAnchorKind.Station,
                        checked((int)empire),
                        new(station.X, station.Y, station.Z),
                        type.Radius ?? 0,
                        StationUndockCatalog.Read(catalog, station.TypeId)
                    )
                );
            }
        }
        return new(sites.ToArray(), missing.ToArray());
    }

    internal void Seed(WorldAction world, CatalogDbContext catalog, int systemId, NpcSecurityBand band, long now)
    {
        if (band == NpcSecurityBand.Null)
        {
            return;
        }
        var anchors = catalog.Definition(s_guardAnchors, systemId);
        foreach (int gate in anchors.MissingGates)
        {
            Report($"gate {gate} has no catalog position.");
        }
        foreach (var anchor in anchors.Sites)
        {
            SeedAnchor(world, catalog, systemId, anchor, now);
        }
    }

    private void SeedAnchor(WorldAction world, CatalogDbContext catalog, int systemId, GuardAnchor site, long now)
    {
        var (anchorId, kind, factionId, anchor, radius, lanes) = site;
        int slots = kind == NpcAnchorKind.Gate ? 3 : 2;
        // A destroyed guard only schedules a respawn when it is the newest occupant of its slot.
        Span<NpcPopulationMember?> latest = stackalloc NpcPopulationMember?[slots];
        latest.Clear();
        foreach (var npc in NpcPopulationMember.AtAnchor(world, anchorId))
        {
            if ((uint)npc.Slot < latest.Length && (latest[npc.Slot] is not { } prior || prior.Id < npc.Id))
            {
                latest[npc.Slot] = npc;
            }
        }
        Span<bool> due = stackalloc bool[slots];
        bool anyDue = false;
        for (int slot = 0; slot < latest.Length; slot++)
        {
            var previous = latest[slot];
            due[slot] =
                previous is null
                || previous.Value.DestroyedAt is long destroyed
                    && now - destroyed >= NpcEncounterDefinitions.RespawnSeconds * TimeSpan.TicksPerSecond;
            anyDue |= due[slot];
        }
        var encounters = world.Set<WorldNpcEncounter>();
        if (
            !anyDue
            && encounters is EconomySet<WorldNpcEncounter, EconomyWorldNpcEncounter> resident
            && resident.ContainsSnapshot(new(anchorId))
        )
        {
            return;
        }
        var encounter = encounters.Find((long)anchorId);
        if (encounter is not null && !anyDue)
        {
            return;
        }
        var roster = NpcGuardRules.Roster(factionId)!;
        NpcGuardType[] types =
            kind == NpcAnchorKind.Gate ? [roster.Cruiser, roster.Frigate, roster.Customs] : [roster.Cruiser, roster.Frigate];
        var members = types.Select(type => Profile(catalog, (int)type, factionId)).ToArray();
        if (members.Any(m => m is null))
        {
            return;
        }
        if (encounter is null)
        {
            var home = NpcGuardRules.Home(anchor, radius, lanes, CatalogObstacles(catalog, systemId));
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
            if (!due[slot])
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
