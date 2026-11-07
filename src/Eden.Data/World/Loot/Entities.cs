using System.Globalization;
using System.Security.Cryptography;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.World.Loot;

public sealed class WorldWreck
{
    public long Id { get; set; }
    public long? NpcId { get; set; }
    public long? ShipId { get; set; }
    public long OwnerId { get; set; }
    public int CorporationId { get; set; }
    public int TypeId { get; set; }
    public int SystemId { get; set; }
    public required string Name { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
}
