using System.Collections.Frozen;
using System.Text.Json;
using Eden.Data.Catalog.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Combat;
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

internal sealed partial class CapsuleerShipBlueprints
{
    internal void Create(
        WorldAction world,
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
        foreach (var skill in blueprint.Skills)
        {
            world.Add(
                new PilotSkill
                {
                    PilotId = pilot,
                    TypeId = skill.TypeId,
                    Points = skill.Points,
                }
            );
        }
        foreach (var item in items)
        {
            world.Add(
                new OwnedItem
                {
                    Id = item.Id,
                    OwnerId = item.OwnerId,
                    TypeId = item.TypeId,
                    LocationId = item.LocationId,
                    FlagId = item.FlagId,
                    Quantity = item.Quantity,
                    Singleton = item.Singleton,
                    Online = item.Online,
                    BlueprintRuns = -1,
                    CrystalDamage = item.CrystalDamage,
                }
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
        world.StageFit(new(pilot, ship, 0), world.FitRevision(pilot), blueprint.Attributes);
    }
}
