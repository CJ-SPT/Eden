using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime.Economy;

namespace Eden.World.Drones;

internal sealed partial class PilotDroneStore
{
    private static void SetFighterTackle(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        WorldDrone row,
        DroneContact contact,
        DroneWork work,
        FighterTackle tackle,
        FighterAbilityTarget? target,
        int system,
        long now
    )
    {
        if (target is null)
        {
            if (work.FighterTackle?.Active(now) != true)
            {
                throw new InvalidDataException("The fighter tackle is no longer active.");
            }
            ClearFighterTackle(world, row);
            return;
        }
        if (
            target.Companion is not { } companion
            || row.Activity == DroneActivity.ReturningToBay
            || work.FighterTackle?.EndsAt > now
            || !PlayerEwarRules.CanTarget(PlayerEwarStore.Active(world, row.ItemId), target.Id)
            || contact.Position.DistanceTo(target.Position) - target.Radius - contact.Profile.Radius > tackle.Range
        )
        {
            throw new InvalidDataException("Tackle a locked hostile drone or fighter within ability range.");
        }
        if (world.Set<WorldPlayerEwar>().Find(row.ItemId, target.Id) is { } previous)
        {
            world.Remove(previous);
        }
        long ends = SpatialModuleRules.Deadline(now, tackle.DurationMilliseconds);
        var power = new NpcEwarPower(WebPercent: tackle.WebPercent, WarpStrength: tackle.WarpStrength);
        var module = new ElectronicWarfareModule(
            row.ItemId,
            contact.Profile.TypeId,
            DogmaEffect.FighterTackle,
            NpcEwarKind.Webifying,
            tackle.DurationMilliseconds,
            0,
            tackle.Range,
            0,
            power
        );
        var effect = new ElectronicWarfareContact(
            pilot.Id,
            pilot.ActiveShipId,
            module,
            new(target.Id, TackleTargetKind.Drone),
            power,
            now,
            ends
        );
        world.Add(
            new WorldPlayerEwar
            {
                ModuleId = row.ItemId,
                PilotId = pilot.Id,
                SourceShipId = pilot.ActiveShipId,
                SystemId = system,
                TargetId = target.Id,
                TargetKind = TackleTargetKind.Drone,
                ContactJson = JsonSerializer.Serialize(effect),
            }
        );
        CrimewatchPersistence.Player(world, catalog, pilot.Id, companion.Owner.Id, system, target.Decision!, now, ends);
        WriteWork(row, work with { FighterTackle = new(target.Id, now, ends) });
    }

    private static void ClearFighterTackle(WorldAction world, WorldDrone drone)
    {
        var work = ReadWork(drone);
        if (work.FighterTackle is not { } active)
        {
            return;
        }
        if (world.Set<WorldPlayerEwar>().Find(drone.ItemId, active.TargetId) is { } row)
        {
            world.Remove(row);
        }
        WriteWork(drone, work with { FighterTackle = active with { StartedAt = 0 } });
    }

    internal static void SweepFighterTackle(WorldAction world, CatalogDbContext catalog, WorldPlayerEwar effect, long now)
    {
        var source = world.Set<WorldDrone>().Find(effect.ModuleId);
        var target = world.Set<WorldDrone>().Find(effect.TargetId);
        var contact = effect.Contact();
        if (
            source is { Activity: not DroneActivity.ReturningToBay }
            && source.ControllerShipId == effect.SourceShipId
            && ReadWork(source).FighterTackle is { } active
            && active.TargetId == effect.TargetId
            && active.Active(now)
            && target is not null
            && source.SystemId == effect.SystemId
            && target.SystemId == effect.SystemId
            && world.Set<OwnedItem>().Find(source.ItemId) is { } sourceItem
            && world.Set<OwnedItem>().Find(target.ItemId) is { } targetItem
        )
        {
            var position = new SpacePosition(source.X, source.Y, source.Z);
            double radii = (catalog.RequireItem(sourceItem.TypeId).Radius ?? 0) + (catalog.RequireItem(targetItem.TypeId).Radius ?? 0);
            if (position.DistanceTo(new(target.X, target.Y, target.Z)) - radii <= contact.Module.Optimal)
            {
                return;
            }
        }
        if (source is not null)
        {
            var work = ReadWork(source);
            if (work.FighterTackle is { } tackle && tackle.TargetId == effect.TargetId)
            {
                WriteWork(source, work with { FighterTackle = tackle with { StartedAt = 0 } });
            }
        }
        world.Remove(effect);
    }

    private static void AdvanceFighterTackle(WorldAction world, CatalogDbContext catalog, WorldDrone drone, long now)
    {
        var work = ReadWork(drone);
        if (work.FighterTackle is not { StartedAt: > 0 } tackle)
        {
            return;
        }
        if (world.Set<WorldPlayerEwar>().Find(drone.ItemId, tackle.TargetId) is { } effect)
        {
            SweepFighterTackle(world, catalog, effect, now);
        }
        else
        {
            WriteWork(drone, work with { FighterTackle = tackle with { StartedAt = 0 } });
        }
    }
}
