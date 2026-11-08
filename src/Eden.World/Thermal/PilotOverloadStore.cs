using System.Collections.Frozen;
using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Thermal;

[Injectable]
internal sealed class PilotOverloadStore(WorldDatabase database) : IPilotOverloading
{
    internal static bool Selected(WorldDbContext world, string path, long pilot, long module)
    {
        var heat = LiveWorld.Find(path)?.Heat;
        if (heat?.Selected(pilot, module) != true)
        {
            return false;
        }
        var item = world.Set<OwnedItem>().Find(module);
        return item?.OwnerId == pilot && heat.Selected(pilot, module, item.LocationId);
    }

    public IReadOnlySet<long> Requested(long pilot, long ship) =>
        LiveWorld.Find(database.Path)?.Heat.Requested(pilot, ship) ?? FrozenSet<long>.Empty;

    public void Clear(long pilot, long ship) => LiveWorld.Find(database.Path)?.Heat.ClearSelection(pilot, ship);

    public IReadOnlyList<ModuleOverloadState> Set(ModuleOverloadCommand command)
    {
        if (
            !Enum.IsDefined(command.Scope)
            || command.Time < 0
            || command.PilotId <= 0
            || command.ShipId <= 0
            || command.ModuleId <= 0
            || command.Scope == OverloadScope.Module && command.EffectId is not > 0
            || command.Scope == OverloadScope.Rack && command.EffectId is not null
        )
        {
            throw new InvalidDataException("Invalid overload command.");
        }
        long pilot = command.PilotId;
        long ship = command.ShipId;
        long module = command.ModuleId;
        int? effect = command.EffectId;
        bool rack = command.Scope == OverloadScope.Rack;
        bool enabled = command.Enabled;
        long now = command.Time;
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var actor =
            world
                .Pilots.Include(p => p.ActiveShip)
                    .ThenInclude(s => s.Position)
                .SingleOrDefault(p => p.Id == pilot && p.ActiveShipId == ship && p.ActiveShip.Position != null)
            ?? throw new InvalidDataException("Overload your own active ship in space.");
        var calculation = FittingCalculation.Read(world, catalog, actor);
        var selected =
            calculation.Modules.SingleOrDefault(m => m.Item.Id == module)
            ?? throw new InvalidDataException("The fitted module is unavailable.");
        if (!selected.Item.Singleton || selected.Item.Quantity != 1)
        {
            throw new InvalidDataException("The fitted module is unavailable.");
        }
        var anchorRack =
            ShipHeatPersistence.Rack(selected.Item.FlagId) ?? throw new InvalidDataException("This item has no overload rack.");
        var profile = OverloadCatalog.Definition(catalog, selected.Item.TypeId);
        if (!rack && profile is null)
        {
            throw new InvalidDataException("This module cannot be overheated.");
        }
        if (effect is not null && effect != profile?.EffectId)
        {
            throw new InvalidDataException("Use the module's own overload effect.");
        }
        var bank = PilotWeaponBankStore.Members(world, ship, module);
        var items = calculation
            .Modules.Where(m => rack ? ShipHeatPersistence.Rack(m.Item.FlagId) == anchorRack : bank.Contains(m.Item.Id))
            .Select(m => (Item: m.Item, Profile: OverloadCatalog.Definition(catalog, m.Item.TypeId)))
            .Where(m =>
                m.Profile is not null
                && (!rack || !enabled || m.Item.Online && !ModuleConditionPersistence.Read(world, catalog, m.Item).BurnedOut)
            )
            .OrderBy(m => m.Item.FlagId)
            .ToArray();
        if (
            enabled
            && (
                world.Set<SavedNaniteRepair>().Any(r => r.ShipId == ship && r.CompletesAt > now)
                || items.Any(m =>
                    !m.Item.Online
                    || ModuleConditionPersistence.Read(world, catalog, m.Item).BurnedOut
                    || calculation.SkillLevels.GetValueOrDefault(OverloadRules.ThermodynamicsSkill) < m.Profile!.RequiredThermodynamics
                )
            )
        )
        {
            throw new InvalidDataException("Overload requires working online modules, Thermodynamics and no active paste repairs.");
        }
        var heat =
            LiveWorld.Find(database.Path)?.Heat ?? throw new InvalidDataException("Overload requires the authoritative world owner.");
        var saved = world.Set<SavedShipHeat>().Find(ship);
        var cycles = saved is null ? Array.Empty<PaidHeatCycle>() : ShipHeatPersistence.Decode(saved.StateJson).Cycles;
        var result = items
            .Select(m => new ModuleOverloadState(
                m.Item.Id,
                m.Profile!.EffectId,
                enabled,
                enabled
                    ? NaniteRepairPersistence.Active(world, m.Item.Id, now) && cycles.All(c => c.ModuleId != m.Item.Id)
                    : cycles.Any(c => c.ModuleId == m.Item.Id)
            ))
            .ToArray();
        transaction.Commit();
        heat.Select(pilot, ship, result.Select(r => r.ModuleId).ToArray(), enabled);
        return Array.AsReadOnly(result);
    }
}
