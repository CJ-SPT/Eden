using Eden.Data.Catalog;
using Eden.Data.World.Flight;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Structures;

namespace Eden.World.Flight;

internal sealed partial class PilotCapitalTravelStore
{
    private sealed record MobileCynoProfile(
        int GroupId,
        string Name,
        float Radius,
        int? FieldType,
        long Activation,
        long Lifetime,
        double MaximumSecurity,
        double LaunchRange,
        double GroupSeparation,
        double StationGateSeparation,
        double TowerSeparation,
        double EssSeparation,
        double InhibitionRange
    );

    private const int StationGroup = 15;
    private const int StargateGroup = 10;
    private const int ControlTowerGroup = 365;
    private const int LegacyEssGroup = 1273;
    private const int EssGroup = 4079;
    private static readonly CatalogDefinition<MobileCynoProfile> s_mobileProfiles = new("mobile-cyno", LoadMobileProfile);

    public CynoDeployment Deploy(long pilotId, long shipId, IReadOnlyList<long> itemIds, SpacePosition heading)
    {
        if (itemIds.Count is 0 or > 16 || itemIds.Distinct().Count() != itemIds.Count || !double.IsFinite(heading.Length))
        {
            throw new InvalidDataException("Choose distinct cynosural deployables in your cargo.");
        }
        using var world = Live.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = ActivePilot(world, pilotId, shipId);
        long now = Now;
        RequireReady(world, pilot, now);
        RequireLiving(world, pilot, PilotFittingStore.ShipAttributes(world, catalog, pilot), now);
        var saved = pilot.ActiveShip.Position!;
        int system = saved.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
        var space =
            catalog.Read(CatalogData.Client.Systems, system)
            ?? throw new InvalidDataException("Deploy in a solar system with known security.");
        var origin = new SpacePosition(saved.X, saved.Y, saved.Z);
        var direction = heading.Length > 0 ? heading.Unit : new(1, 0, 0);
        long fleet = fleets.Identity(pilotId)?.FleetId ?? 0;
        var existing = MobileRows(world, system, now).ToList();
        var geometry = SystemObjects.Geometry(catalog, system);
        Dictionary<long, long> launched = [];
        List<InventoryChange> changes = [];
        world.RemoveRange(world.Filter<SavedCynoDeployable>(row => row.ExpiresAt <= now).ToArray());
        foreach (long id in itemIds)
        {
            if (
                world.Set<OwnedItem>().Find(id) is not { Quantity: > 0 } item
                || item.OwnerId != pilotId
                || item.LocationId != shipId
                || item.FlagId != (int)InventoryFlag.Cargo
                || item.TypeId != CapitalTravelRules.MobileInhibitor && MobileFieldType(item.TypeId) is null
            )
            {
                throw new InvalidDataException("Choose a supported mobile cynosural deployable from your active ship's cargo.");
            }
            var profile = catalog.Definition(s_mobileProfiles, item.TypeId);
            if (space.SecurityStatus is not double security || security > profile.MaximumSecurity)
            {
                throw new InvalidDataException("This deployable requires eligible low-security or null-security space.");
            }
            PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
            var point = origin + direction * profile.LaunchRange;
            if (profile.FieldType is int fieldType)
            {
                RequireCynoSystem(catalog, system);
                if (fleet == 0)
                {
                    throw new InvalidDataException("Join a fleet before deploying a mobile cynosural beacon.");
                }
                if (!CapitalTravelRules.Covert(fieldType))
                {
                    RequireIgnitionUnjammed(world, catalog, system, now);
                    if (Inhibited(world, catalog, system, point, now))
                    {
                        throw new InvalidDataException("Cynosural fields are inhibited at this deployment position.");
                    }
                }
            }
            if (
                existing.Any(row =>
                    (row.TypeId == item.TypeId || profile.FieldType.HasValue && MobileFieldType(row.TypeId).HasValue)
                    && point.DistanceTo(MobilePosition(row)) < profile.GroupSeparation
                ) || geometry.Any(body => MobileObstructed(profile, point, body))
            )
            {
                throw new InvalidDataException("This position is too close to another object for deployment.");
            }
            var deployed = new SavedCynoDeployable
            {
                Id = world.NextItemId(),
                BeaconId = profile.FieldType.HasValue ? world.NextItemId() : 0,
                PilotId = pilotId,
                FleetId = profile.FieldType.HasValue ? fleet : 0,
                SystemId = system,
                TypeId = item.TypeId,
                OnlineAt = checked(now + profile.Activation),
                ExpiresAt = checked(now + profile.Lifetime),
                X = point.X,
                Y = point.Y,
                Z = point.Z,
            };
            world.Add(deployed);
            existing.Add(deployed);
            launched.Add(id, deployed.Id);
            changes.AddRange(SpendFuel(world, catalog, [item], 1));
        }
        world.Commit();
        return new(launched, changes);
    }

    public IReadOnlyList<StaticSpaceObject> Deployables(int systemId)
    {
        long now = Now;
        if (LiveWorld.Find(database.Path) is not { Economy: var economy } || !economy.MayHaveCynos(now))
        {
            return [];
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return economy.Inspect(
            world =>
                MobileRows(world, systemId, now)
                    .AsEnumerable()
                    .Select(row =>
                    {
                        var profile = catalog.Definition(s_mobileProfiles, row.TypeId);
                        var defense = ShipHealth.DefenseFrom(PilotFittingStore.ReadAttributes(catalog, row.TypeId));
                        return new StaticSpaceObject(
                            row.Id,
                            row.TypeId,
                            profile.GroupId,
                            (int)InventoryCategory.Deployable,
                            checked((int)row.PilotId),
                            MobilePosition(row),
                            profile.Radius
                        )
                        {
                            IsGlobal = false,
                            Name = profile.Name,
                            CynoDeployable = new(
                                row.OnlineAt <= now,
                                row.OnlineAt,
                                row.ExpiresAt,
                                new(row.Id, defense, MobileHealth(row, defense, now)),
                                row.DamagedAt
                            ),
                        };
                    })
                    .ToArray(),
            new(systems: [systemId])
        );
    }

    private static IQueryable<SavedCynoDeployable> MobileRows(WorldAction world, int system, long now) =>
        world.Filter<SavedCynoDeployable>(row => row.SystemId == system && row.ExpiresAt > now);

    internal static SavedCynoDeployable? MobileTarget(WorldAction world, long id, long now) =>
        world.Set<SavedCynoDeployable>().Find(id) is { } row && row.ExpiresAt > now ? row : null;

    internal static SpacePosition MobilePosition(SavedCynoDeployable row) => new(row.X, row.Y, row.Z);

    internal static CombatDamageSettlement DamageMobile(
        WorldAction world,
        SavedCynoDeployable row,
        HullDefense defense,
        DamageVector damage,
        long now
    )
    {
        var result = CombatDamage.Apply(MobileHealth(row, defense, now), defense, damage, row.Id, now);
        var remaining = result.Remaining;
        if (remaining.Destroyed)
        {
            world.Remove(row);
        }
        else if (result.Applied > 0)
        {
            (row.Shield, row.Armor, row.Structure) = (remaining.Shield, remaining.Armor, remaining.Structure);
            row.DamagedAt = now;
        }
        return new(new(row.Id, defense, remaining), result.Applied, remaining.Destroyed, null);
    }

    private static HullDamage MobileHealth(SavedCynoDeployable row, HullDefense defense, long now) =>
        new(
            row.Shield is double shield
                ? ChargedResource.Recharged(
                    shield,
                    defense.Shield,
                    defense.ShieldRechargeMilliseconds,
                    Math.Max(0, (now - row.DamagedAt) / (double)TimeSpan.TicksPerMillisecond)
                )
                : defense.Shield,
            row.Armor ?? defense.Armor,
            row.Structure ?? defense.Structure
        );

    private static bool Inhibited(WorldAction world, CatalogDbContext catalog, int system, SpacePosition position, long now)
    {
        double range = 0;
        foreach (
            var row in MobileRows(world, system, now).Where(row => row.TypeId == CapitalTravelRules.MobileInhibitor && row.OnlineAt <= now)
        )
        {
            range = range > 0 ? range : catalog.Definition(s_mobileProfiles, CapitalTravelRules.MobileInhibitor).InhibitionRange;
            if (position.DistanceTo(MobilePosition(row)) <= range)
            {
                return true;
            }
        }
        return false;
    }

    private static bool MobileObstructed(MobileCynoProfile profile, SpacePosition position, StaticSpaceObject body)
    {
        double clear = body.GroupId switch
        {
            StationGroup or StargateGroup => profile.StationGateSeparation,
            ControlTowerGroup => profile.TowerSeparation,
            LegacyEssGroup or EssGroup => profile.EssSeparation,
            _ => 0,
        };
        return position.DistanceTo(body.Position) < (body.Solid ? body.Radius + profile.Radius : 0) + clear;
    }

    private static bool MobileBeaconActive(SavedCynoDeployable row, long now) =>
        row.BeaconId != 0 && row.OnlineAt <= now && row.ExpiresAt > now && MobileFieldType(row.TypeId).HasValue;

    private static int? MobileFieldType(int typeId) =>
        typeId switch
        {
            CapitalTravelRules.MobileBeacon => CapitalTravelRules.MobileField,
            CapitalTravelRules.CovertMobileBeacon => CapitalTravelRules.CovertMobileField,
            _ => null,
        };

    private static CynosuralField MobileBeaconContact(SavedCynoDeployable row)
    {
        int type = MobileFieldType(row.TypeId)!.Value;
        bool covert = CapitalTravelRules.Covert(type);
        return new(
            row.PilotId,
            0,
            0,
            row.FleetId,
            row.SystemId,
            row.OnlineAt,
            row.ExpiresAt,
            new(
                row.BeaconId,
                type,
                covert ? CapitalTravelRules.CovertFieldGroup : CapitalTravelRules.FieldGroup,
                (int)InventoryCategory.Celestial,
                0,
                MobilePosition(row),
                0
            )
            {
                Solid = false,
                IsGlobal = !covert,
                Name = covert ? "Covert Cynosural Field" : "Mobile Cynosural Beacon Field",
            },
            row.Id
        );
    }

    private static MobileCynoProfile LoadMobileProfile(CatalogDbContext catalog, int typeId)
    {
        var type = catalog.FittingType(typeId) ?? throw new InvalidDataException("Missing mobile cynosural deployable type.");
        var fields = catalog.Require(CatalogData.Client.Spacecomponentsbytype, (long)typeId);
        double Read(string component, string key) =>
            fields.GetValueOrDefault(component)?.GetValueOrDefault(key) switch
            {
                ClientSpacecomponentsbytypeValueValueChoiceLong value => value.Value,
                ClientSpacecomponentsbytypeValueValueChoiceDouble value => value.Value,
                _ => 0,
            };
        long Seconds(string component) => (long)(Read(component, "durationSeconds") * TimeSpan.TicksPerSecond);
        if (Seconds("activate") <= 0 || Seconds("decay") <= Seconds("activate"))
        {
            throw new InvalidDataException("The mobile cynosural deployable has an invalid activation or lifetime.");
        }
        return new(
            type.GroupId,
            type.Name,
            (float)(type.Radius ?? 0),
            MobileFieldType(typeId),
            Seconds("activate"),
            Seconds("decay"),
            Read("deploy", "maxSecurityLevel"),
            Read("deploy", "deployAtRange"),
            Read("deploy", "minDistanceFromOwnGroup"),
            Read("deploy", "minDistanceFromStargatesAndStations"),
            Read("deploy", "minDistanceFromControlTower"),
            Read("deploy", "minDistanceFromESS"),
            Read("cynoInhibitor", "range")
        );
    }
}
