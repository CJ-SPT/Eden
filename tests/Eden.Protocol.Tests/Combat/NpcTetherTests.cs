using Eden.Data.World.Combat;
using Eden.Data.World.Structures;
using Eden.Gameplay.Structures;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class NpcEwarTests
{
    [Fact]
    public void TetheredShipsCannotReceiveANewNpcWarpDisruptionAttempt()
    {
        using Fixture f = new(13032, resident: true);
        PassiveCatalogFixture.LoadStructures(new WorldDatabase(f.Combat.Db.Path).CatalogPath);
        var pilot = f.Combat.Pilot;
        using (WorldActionScope action = new(new(f.Combat.Db.Path), new(systems: [pilot.SystemId])))
        {
            action.World.Add(
                new SavedStructure
                {
                    Id = StructureRules.FirstStructureId + 1,
                    TypeId = 35832,
                    Name = "Tether test",
                    SystemId = pilot.SystemId,
                    State = StructureState.ShieldVulnerable,
                    HasCore = true,
                    PublicAccess = true,
                    X = pilot.Position!.X,
                    Y = pilot.Position.Y,
                    Z = pilot.Position.Z,
                }
            );
            action.Commit();
        }
        var tethering = f.Combat.Db.Services.GetRequiredService<IPilotTethering>();
        Assert.NotNull(tethering.Advance(pilot.PilotId, new(true, false, false, false, false)));
        using (WorldActionScope action = new(new(f.Combat.Db.Path), new(systems: [pilot.SystemId])))
        {
            action.World.Add(
                new NpcAggression
                {
                    NpcId = f.Combat.Npc.Id,
                    TargetShipId = pilot.ShipId,
                    ReadyAt = f.Now,
                }
            );
            action.Commit();
        }
        int rolls = f.Roll.Count;
        Assert.Empty(f.Warfare.Advance(pilot.PilotId, pilot.ShipId, f.Now));
        Assert.Equal(rolls, f.Roll.Count);
        Assert.True(tethering.Protected(pilot.ShipId));
    }
}
