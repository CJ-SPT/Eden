using System.Collections.Frozen;
using System.Text.Json;
using Eden.Data.Catalog.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

// New pilots use these blueprints. Background validation runs outside the live world owner.
// Existing pilots never receive copied assets or skills.
internal sealed partial class CapsuleerShipBlueprints : IDisposable
{
    private const int MaximumBlueprints = 128;
    private const long TemplatePilot = 90000001;
    private const long TemplateShip = 140000000001;
    private const long TemplateTime = TimeSpan.TicksPerDay;
    private const int ShipContainer = -1;
    private const int StationContainer = -2;
    private readonly Dictionary<(int Race, string Fit), Blueprint> _blueprints = [];
    private readonly Dictionary<(int Race, string Fit), Blueprint> _prepared = [];

    internal void BeginPreparation() => _prepared.Clear();

    private readonly int _station;

    private sealed record Skill(int TypeId, long Points);

    private sealed record Item(int TypeId, int FlagId, int Quantity, bool Singleton, bool Online, int Container, double CrystalDamage);

    private sealed record Cooldown(int Item, long Delay, ModuleCooldownKind Kind);

    private sealed record Health(double ShieldCharge, double ArmorDamage, double StructureDamage);

    private sealed record Blueprint(
        Skill[] Skills,
        Item[] Items,
        Cooldown[] Cooldowns,
        Health? Health,
        double? Capacitor,
        FrozenDictionary<DogmaAttributeId, double> Attributes
    )
    {
        internal IReadOnlyDictionary<DogmaAttributeId, double> AttributesFor(MiningHoldPolicy policy)
        {
            if (policy is { GeneralMultiplier: 1, IceMultiplier: 1, GasMultiplier: 1 })
            {
                return Attributes;
            }
            // The fitting preview uses default world settings.
            var tuned = Attributes.ToDictionary();
            WorldTuningRules.ScaleMiningHolds(policy, tuned);
            return tuned;
        }
    }

    private sealed record InventoryGrant(
        long Id,
        long OwnerId,
        int TypeId,
        long LocationId,
        int FlagId,
        int Quantity,
        bool Singleton,
        bool Online,
        double CrystalDamage
    );

    internal CapsuleerShipBlueprints(CatalogDbContext catalog) => _station = catalog.Definitions.Stations.Keys.First();

    internal void Create(
        WorldDbContext world,
        CatalogDbContext catalog,
        int race,
        int hull,
        TrafficProfile? profile,
        long pilot,
        long ship,
        int station,
        long now,
        bool requirePrepared = false,
        bool mining = false
    )
    {
        string signature =
            mining ? $"mining:{hull}"
            : profile is null ? $"generated:{hull}"
            : TrafficSnapshot.Signature(profile);
        if (!(requirePrepared ? _prepared : _blueprints).TryGetValue((race, signature), out var blueprint))
        {
            if (requirePrepared)
            {
                throw new CapsuleerBlueprintNotPreparedException();
            }
            blueprint = Build(catalog, race, hull, profile, mining);
            Retain((race, signature), blueprint);
        }
        // The caller reserved the next shared inventory ID for this new ship under
        // the transaction owner. No inventory is inserted before its blueprint.
        long first = checked(ship + 1);
        var items = blueprint
            .Items.Select(
                (item, index) =>
                    new InventoryGrant(
                        checked(first + index),
                        pilot,
                        item.TypeId,
                        item.Container == ShipContainer ? ship
                            : item.Container == StationContainer ? station
                            : checked(first + item.Container),
                        item.FlagId,
                        item.Quantity,
                        item.Singleton,
                        item.Online,
                        item.CrystalDamage
                    )
            )
            .ToArray();
        string skills = JsonSerializer.Serialize(blueprint.Skills);
        world.Database.ExecuteSql(
            $"""
            INSERT INTO WorldPilotSkill(PilotId,TypeId,Points)
            SELECT {pilot},json_extract(value,'$.TypeId'),json_extract(value,'$.Points') FROM json_each({skills})
            """
        );
        if (items.Length > 0)
        {
            string inventory = JsonSerializer.Serialize(items);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldItem(Id,OwnerId,TypeId,LocationId,FlagId,Quantity,Singleton,Online,
                    MaterialEfficiency,TimeEfficiency,BlueprintRuns,CrystalDamage)
                SELECT json_extract(value,'$.Id'),json_extract(value,'$.OwnerId'),json_extract(value,'$.TypeId'),
                    json_extract(value,'$.LocationId'),json_extract(value,'$.FlagId'),json_extract(value,'$.Quantity'),
                    json_extract(value,'$.Singleton'),json_extract(value,'$.Online'),0,0,-1,json_extract(value,'$.CrystalDamage')
                FROM json_each({inventory})
                """
            );
        }
        foreach (var cooldown in blueprint.Cooldowns)
        {
            world.Add(
                new ModuleCooldown
                {
                    ModuleId = checked(first + cooldown.Item),
                    Kind = cooldown.Kind,
                    ReadyAt = checked(now + cooldown.Delay),
                }
            );
        }
        if (blueprint.Health is { } health)
        {
            world.Add(
                new ShipHealthState
                {
                    ShipId = ship,
                    ShieldCharge = health.ShieldCharge,
                    ArmorDamage = health.ArmorDamage,
                    StructureDamage = health.StructureDamage,
                    UpdatedAt = now,
                }
            );
        }
        if (blueprint.Capacitor is double energy)
        {
            world.Add(
                new ShipEnergy
                {
                    ShipId = ship,
                    Charge = energy,
                    UpdatedAt = now,
                }
            );
        }
        world.Add(new Eden.Data.World.Capsuleers.SavedCapsuleerSupplies { PilotId = pilot, ShipId = ship });
        world.SaveChanges();
        // Publish through the normal revision/transaction cache. Navigation need not rebuild the validated new fit.
        world.StageFit(new(pilot, ship, 0), world.FitRevision(pilot), blueprint.AttributesFor(world.Settings.MiningHolds));
    }

    internal void Prepare(CatalogDbContext catalog, int race, int hull, TrafficProfile? profile, bool mining = false)
    {
        string signature =
            mining ? $"mining:{hull}"
            : profile is null ? $"generated:{hull}"
            : TrafficSnapshot.Signature(profile);
        if (_blueprints.TryGetValue((race, signature), out var cached))
        {
            _prepared[(race, signature)] = cached;
            return;
        }
        var blueprint = Build(catalog, race, hull, profile, mining);
        Retain((race, signature), blueprint);
        _prepared[(race, signature)] = blueprint;
    }

    private void Retain((int Race, string Fit) key, Blueprint blueprint)
    {
        while (_blueprints.Count >= MaximumBlueprints)
        {
            var cold = _blueprints.Keys.Where(k => !_prepared.ContainsKey(k)).Take(1).ToArray();
            if (cold.Length == 0)
            {
                break;
            }
            _blueprints.Remove(cold[0]);
        }
        _blueprints.Add(key, blueprint);
    }

    private Blueprint Build(CatalogDbContext catalog, int race, int hull, TrafficProfile? profile, bool mining)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("startup.shipBlueprint");
        using var world = ResidentEconomy.PreviewFit(catalog);
        world.Add(
            new Account
            {
                Id = 1,
                UserName = "Population blueprint",
                CloneGrade = AccountCloneGrade.Omega,
            }
        );
        world.Add(new InventoryOwner { Id = TemplatePilot });
        world.Add(
            new Ship
            {
                Id = TemplateShip,
                OwnerId = TemplatePilot,
                StationId = _station,
                TypeId = hull,
                Name = "Population blueprint",
            }
        );
        world.Add(
            new Pilot
            {
                Id = TemplatePilot,
                AccountId = 1,
                ActiveShipId = TemplateShip,
                RaceId = race,
                Name = "Population blueprint",
            }
        );
        world.SaveChanges();
        StartingSkills.Grant(world, catalog, TemplatePilot, race);
        CapsuleerCommunityStore.GrantRequirements(world, catalog, TemplatePilot, hull, []);
        if (mining)
        {
            long module = InventoryPersistence.NextId(world);
            CapsuleerCommunityStore.GrantRequirements(world, catalog, TemplatePilot, (int)MiningLaserType.MinerI, []);
            world.Add(
                new OwnedItem
                {
                    Id = module,
                    OwnerId = TemplatePilot,
                    TypeId = (int)MiningLaserType.MinerI,
                    LocationId = _station,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 1,
                }
            );
            world.SaveChanges();
            PilotFittingStore.Fit(world, catalog, TemplatePilot, [module], _station, (int)InventoryFlag.AutoFit, TemplateTime);
            PilotFittingStore.SetOnline(world, catalog, TemplatePilot, TemplateShip, module, true, TemplateTime);
        }
        else if (profile is not null)
        {
            TrafficFitting.Apply(world, catalog, TemplatePilot, TemplateShip, profile, TemplateTime);
        }
        else
        {
            const int StarterOreUnits = 100;
            world.Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = TemplatePilot,
                    TypeId = CapsuleerProvisioningCatalog.FreightOre(catalog),
                    LocationId = TemplateShip,
                    FlagId = (int)InventoryFlag.Cargo,
                    Quantity = StarterOreUnits,
                }
            );
            world.SaveChanges();
        }
        CapsuleerSupplyStore.Ensure(world, catalog, TemplatePilot, TemplateShip);
        world.SaveChanges();
        var items = world.Set<OwnedItem>().OrderBy(item => item.Id).ToArray();
        var indices = items.Select((item, index) => (item.Id, Index: index)).ToDictionary(item => item.Id, item => item.Index);
        var health = world.Set<ShipHealthState>().SingleOrDefault();
        var energy = world.Set<ShipEnergy>().SingleOrDefault();
        var blueprint = new Blueprint(
            world.Set<PilotSkill>().OrderBy(skill => skill.TypeId).Select(skill => new Skill(skill.TypeId, skill.Points)).ToArray(),
            items
                .Select(item => new Item(
                    item.TypeId,
                    item.FlagId,
                    item.Quantity,
                    item.Singleton,
                    item.Online,
                    item.LocationId == TemplateShip ? ShipContainer
                        : item.LocationId == _station ? StationContainer
                        : indices[item.LocationId],
                    item.CrystalDamage
                ))
                .ToArray(),
            world
                .Set<ModuleCooldown>()
                .AsEnumerable()
                .Select(cooldown => new Cooldown(indices[cooldown.ModuleId], cooldown.ReadyAt - TemplateTime, cooldown.Kind))
                .ToArray(),
            health is null ? null : new(health.ShieldCharge, health.ArmorDamage, health.StructureDamage),
            energy?.Charge,
            PilotFittingStore.ShipAttributes(world, catalog, TemplatePilot).ToFrozenDictionary()
        );
        return blueprint;
    }

    public void Dispose()
    {
        _prepared.Clear();
        _blueprints.Clear();
    }
}

internal sealed class CapsuleerBlueprintNotPreparedException : Exception { }
