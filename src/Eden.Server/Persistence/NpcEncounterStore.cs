using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Ships;
using Eden.Protocol;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Mining;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class WorldNpc
{
    public long Id { get; set; }
    public int BeltId { get; set; }
    public int SystemId { get; set; }
    public int TypeId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double ShieldCharge { get; set; }
    public double ArmorDamage { get; set; }
    public double StructureDamage { get; set; }
    public long UpdatedAt { get; set; }
    public long? DestroyedAt { get; set; }
    public double VelocityX { get; set; }
    public double VelocityY { get; set; }
    public double VelocityZ { get; set; }
    public long MotionAt { get; set; }
    public NpcNavigationState Navigation { get; set; }
}

[Injectable]
internal sealed class NpcEncounterStore(WorldDatabase database) : INpcEncounters
{
    public IReadOnlyList<NpcContact> List(int systemId, long now)
    {
        if (systemId != NpcEncounterDefinitions.PerimeterSystem)
        {
            return [];
        }
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        using var transaction = world.Database.BeginTransaction();
        var belts = catalog
            .Set<Celestial>()
            .Where(c => c.SolarSystemId == systemId && c.GroupId == AsteroidDefinitions.BeltGroup)
            .OrderBy(c => c.Id)
            .ToArray();
        if (belts.Length == 0)
        {
            return [];
        }
        var type = catalog.Set<ItemType>().Include(t => t.Group).Single(t => t.Id == NpcEncounterDefinitions.GuristasRookieType);
        var attributes = catalog
            .Set<TypeAttribute>()
            .Where(a => a.ItemTypeId == type.Id)
            .ToDictionary(a => (DogmaAttributeId)a.AttributeId, a => a.Value);
        var defense = ShipHealth.DefenseFrom(attributes);
        foreach (var belt in belts)
        {
            // Keep defeated rows for bounty identity. A replacement gets a new
            // AUTOINCREMENT ID only after the local ten-minute respawn delay.
            var previous = world.Set<WorldNpc>().Where(n => n.BeltId == belt.Id).OrderByDescending(n => n.Id).FirstOrDefault();
            if (
                previous is not null
                && (
                    !previous.DestroyedAt.HasValue
                    || now - previous.DestroyedAt.Value < TimeSpan.TicksPerSecond * NpcEncounterDefinitions.RespawnSeconds
                )
            )
            {
                continue;
            }
            world
                .Set<WorldNpc>()
                .Add(
                    new()
                    {
                        Id = previous is null ? checked(NpcEncounterDefinitions.FirstNpcId + belt.Id) : 0,
                        BeltId = belt.Id,
                        SystemId = systemId,
                        TypeId = type.Id,
                        X = belt.X + NpcEncounterDefinitions.AnchorOffset,
                        Y = belt.Y,
                        Z = belt.Z,
                        ShieldCharge = defense.Shield,
                        UpdatedAt = now,
                    }
                );
        }
        world.SaveChanges();
        var aggression = world.Set<NpcAggression>().AsNoTracking().ToDictionary(a => a.NpcId);
        NpcWeaponContact? Weapon(long npcId)
        {
            double duration = attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime);
            return
                aggression.TryGetValue(npcId, out var active)
                && active.LastShotAt is long started
                && now >= started
                && now - started < duration * TimeSpan.TicksPerMillisecond
                ? new(active.TargetShipId, checked((int)attributes.GetValueOrDefault(DogmaAttributeId.EntityTurretType)), started, duration)
                : null;
        }
        var npcs = world.Set<WorldNpc>().Where(n => n.SystemId == systemId && n.DestroyedAt == null).OrderBy(n => n.Id).ToArray();
        foreach (var npc in npcs)
        {
            if (NpcMotion.Engaged(npc) && !aggression.ContainsKey(npc.Id))
            {
                NpcMotion.Return(npc);
            }
            if (npc.Navigation == NpcNavigationState.Returning)
            {
                NpcMotion.Advance(npc, NpcMotion.Home(belts.Single(b => b.Id == npc.BeltId)), type, attributes, now, returning: true);
            }
        }
        world.SaveChanges();
        var contacts = npcs.Select(n =>
                n.TypeId != type.Id
                    ? throw new InvalidDataException($"Unsupported saved NPC type {n.TypeId}.")
                    : new NpcContact(
                        n.Id,
                        n.TypeId,
                        type.GroupId,
                        type.Group.CategoryId,
                        NpcEncounterDefinitions.GuristasCorporation,
                        type.Name,
                        systemId,
                        new(n.X, n.Y, n.Z),
                        type.Mass ?? 0,
                        (float)(type.Radius ?? 0),
                        (float)attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity),
                        NpcMotion.Agility(attributes),
                        new(
                            n.Id,
                            defense,
                            new(
                                ChargedResource.Recharged(
                                    n.ShieldCharge,
                                    defense.Shield,
                                    defense.ShieldRechargeMilliseconds,
                                    Math.Max(0, (now - n.UpdatedAt) / (double)TimeSpan.TicksPerMillisecond)
                                ),
                                Math.Max(0, defense.Armor - n.ArmorDamage),
                                Math.Max(0, defense.Structure - n.StructureDamage)
                            )
                        ),
                        Weapon(n.Id),
                        NpcMotion.Contact(
                            n,
                            attributes,
                            aggression.GetValueOrDefault(n.Id)?.TargetShipId ?? 0,
                            NpcMotion.Home(belts.Single(b => b.Id == n.BeltId))
                        )
                    )
            )
            .ToArray();
        transaction.Commit();
        return contacts;
    }

    public bool WasDestroyed(long npcId)
    {
        using WorldDbContext world = new(database.Path);
        return world.Set<WorldNpc>().Any(n => n.Id == npcId && n.DestroyedAt != null);
    }
}
