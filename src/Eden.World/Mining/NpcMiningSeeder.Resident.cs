using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Mining;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Mining;

internal sealed partial class NpcMiningSeeder
{
    internal static void Alert(WorldAction world, WorldNpc attacked, long shipId, long now)
    {
        var operation = world.Set<WorldNpcMiningOperation>().Find(attacked.EncounterId);
        if (operation is null || operation.Stage == NpcMiningStage.Cooldown)
        {
            return;
        }
        if (operation.Stage != NpcMiningStage.Alarm)
        {
            operation.Stage = NpcMiningStage.Alarm;
            operation.ThreatShipId = shipId;
            operation.ResponseAt = checked(now + NpcMiningRules.ResponseSeconds * TimeSpan.TicksPerSecond);
            operation.ResetAt = checked(now + NpcMiningRules.DefenseLifetimeSeconds * TimeSpan.TicksPerSecond);
            (operation.AlertX, operation.AlertY, operation.AlertZ) = (attacked.X, attacked.Y, attacked.Z);
        }
        foreach (
            var defender in world
                .Set<WorldNpc>()
                .Where(n =>
                    n.EncounterId == operation.EncounterId
                    && n.Role == NpcRole.MiningDefender
                    && n.DestroyedAt == null
                    && n.RetiredAt == null
                )
                .ToArray()
        )
        {
            if (world.Set<NpcAggression>().Find(defender.Id) is null)
            {
                world.Add(NpcAggression.Start(defender.Id, shipId, 0, now));
            }
        }
    }
}
