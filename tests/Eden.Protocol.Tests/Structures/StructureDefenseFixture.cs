using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Structures;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

internal static class StructureDefenseFixture
{
    private const int NpcType = 601;
    internal const long Npc = 59000001;
    internal const double NpcCapacity = 30000;
    internal const double Signature = 400;

    internal static PilotCallContext Session(Fixture f, IServiceScope scope, int account, int session)
    {
        var pilot = f.Db.Store.FindPilot(account)!;
        PilotCallContext context = new(
            account,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            session,
            SignedSettings.Empty
        );
        context.Select(pilot);
        return context;
    }

    internal static (StructureSnapshot, long) Prepare(Fixture f, int type)
    {
        var structure = f.Online();
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        long module = f.Item(type);
        f.Db.Services.GetRequiredService<IPilotStructureFitting>().Fit(f.PilotId, structure.Id, [new(module)]);
        return (structure, module);
    }

    internal static void AddNpc(Fixture f, StructureSnapshot source)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true);
        foreach (
            var (attribute, value) in new[]
            {
                (DogmaAttributeId.CapacitorCapacity, NpcCapacity),
                (DogmaAttributeId.CapacitorRechargeTime, 60000d),
                (DogmaAttributeId.SignatureRadius, Signature),
            }
        )
        {
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES ({NpcType},{(int)attribute},{value})");
        }
        using var action = StructureAttackTests.Scope(f);
        action.World.Add(
            new WorldNpc
            {
                Id = Npc,
                TypeId = NpcType,
                SystemId = Fixture.SystemId,
                X = source.Position.X + source.Profile.Radius + 1000,
                HomeX = source.Position.X + source.Profile.Radius + 1000,
                HomeY = 0,
                HomeZ = 0,
                UpdatedAt = f.Clock.Now,
                MotionAt = f.Clock.Now,
            }
        );
        action.Commit();
    }

    internal static void Lock(Fixture f, StructureControlToken token, long target)
    {
        var targeting = f.Db.Services.GetRequiredService<IPilotStructureTargeting>();
        var scan = Assert.Single(targeting.Add(token, target).Targets);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(scan.ReadyAt - f.Clock.Now));
        targeting.RequireLocked(token, target);
    }
}
