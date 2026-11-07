using System.Collections;
using System.Security.Cryptography;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Server;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class ServerPerformanceReplayTests
{
    [Fact]
    public async Task ReplaySavedDogmaCalls()
    {
        string? source = Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_SOURCE");
        if (source is null)
        {
            output.WriteLine("Set EDEN_PERFORMANCE_SOURCE to replay an isolated saved world.");
            return;
        }
        long pilotId = long.Parse(Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_PILOT") ?? "90000006");
        string directory = CopySnapshot(source);
        try
        {
            TestClock clock = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
            WorldDatabase database = new(Path.Combine(directory, "world.sqlite"));
            WorldInitialization.Initialize(database, clock);
            using var services = ServerServices.Build(database, clock, liveWorld: true);
            using var scope = services.CreateScope();
            var provider = scope.ServiceProvider;
            var pilots = provider.GetRequiredService<IPilotLookup>();
            long account;
            using (WorldDbContext world = new(database.Path))
            {
                account = world.Pilots.Single(p => p.Id == pilotId).AccountId;
            }
            List<string> reports = [];
            using PilotCallContext context = new(
                account,
                pilots.ReadAccount(account),
                pilots.ListPilots(account),
                provider.GetRequiredService<FlightSessionFactory>().Create,
                42,
                SignedSettings.Empty
            );
            List<object?> packets = [];
            PilotSessionOutput sessionOutput = new(
                context,
                account,
                42,
                packet =>
                {
                    packets.Add(packet);
                    return Task.CompletedTask;
                },
                reports.Add
            );
            PilotSessionCalls calls = new(
                context,
                sessionOutput,
                provider.GetRequiredService<PilotCallRouter>(),
                provider.GetRequiredService<PilotUpdateCoordinator>(),
                provider.GetRequiredService<SkillHandler>(),
                clock,
                reports.Add,
                account,
                42,
                provider.GetRequiredService<IPilotOperationScope>()
            );
            using (SessionPerformance.Begin("selection", reports.Add, force: true))
            {
                await calls.ProcessAsync(Request("charUnboundMgr", "SelectCharacterID", pilotId));
            }
            Assert.True(context.Selected);
            for (int sample = 0; sample < 6; sample++)
            {
                foreach (string method in new[] { "GetAllInfo", "ItemGetInfo" })
                {
                    using var metrics = SessionPerformance.Begin($"dogma {method} sample {sample}", reports.Add, force: true);
                    object?[] arguments = method == "ItemGetInfo" ? [context.Pilot.ShipId] : [];
                    await calls.ProcessAsync(Request("dogmaIM", "MachoBindObject", null, new object?[] { method, arguments }));
                }
            }
            // Compare values across revisions independently of dictionary enumeration order.
            var dogma = Assert
                .IsType<PilotCallResult.Success>(
                    provider
                        .GetRequiredService<PilotCallRouter>()
                        .Call(context, "dogmaIM", "GetAllInfo", Array.Empty<object?>(), reports.Add)
                )
                .Reply;
            byte[] canonical = BlueMarshal.Encode(CanonicalDogma(dogma));
            reports.Add($"Dogma payload SHA256: {Convert.ToHexString(SHA256.HashData(canonical))}");
            var fittedModule = provider
                .GetRequiredService<IPilotInventory>()
                .List(pilotId, context.Pilot.ShipId)
                .Single(i => i.GroupId == Eden.Gameplay.Propulsion.PropulsionDefinitions.AfterburnerGroup);
            using (SessionPerformance.Begin("offline", reports.Add, force: true))
            {
                await calls.ProcessAsync(Request("dogmaIM", "Deactivate", fittedModule.Id, "online"));
            }
            using (SessionPerformance.Begin("online", reports.Add, force: true))
            {
                await calls.ProcessAsync(Request("dogmaIM", "Activate", fittedModule.Id, "online", null, 0L));
            }
            Assert.True(
                provider
                    .GetRequiredService<IPilotInventory>()
                    .List(pilotId, context.Pilot.ShipId)
                    .Single(i => i.Id == fittedModule.Id)
                    .Online
            );
            long incomingShip;
            using (WorldDbContext world = new(database.Path))
            {
                incomingShip = world
                    .Ships.Where(s => s.OwnerId == pilotId && s.Id != context.Pilot.ShipId && s.TypeId == context.Pilot.ShipTypeId)
                    .OrderBy(s => s.Id)
                    .First()
                    .Id;
            }
            using (SessionPerformance.Begin("boarding", reports.Add, force: true))
            {
                await calls.ProcessAsync(
                    Request(
                        "ship",
                        "MachoBindObject",
                        new object?[] { (long)context.Pilot.StationId, 15L },
                        new object?[] { "Board", new object?[] { incomingShip, context.Pilot.ShipId } }
                    )
                );
            }
            Assert.Equal(incomingShip, context.Pilot.ShipId);
            using (SessionPerformance.Begin("undocking", reports.Add, force: true))
            {
                await calls.ProcessAsync(
                    Request(
                        "ship",
                        "MachoBindObject",
                        new object?[] { (long)context.Pilot.StationId, 15L },
                        new object?[] { "Undock", new object?[] { context.Pilot.ShipId } }
                    )
                );
            }
            Assert.NotNull(context.Pilot.Position);
            clock.Seconds += 20;
            context.Flight.Snapshot();
            var module = provider
                .GetRequiredService<IPilotInventory>()
                .List(pilotId, incomingShip)
                .Single(i => i.GroupId == Eden.Gameplay.Propulsion.PropulsionDefinitions.AfterburnerGroup);
            using (SessionPerformance.Begin("activation", reports.Add, force: true))
            {
                await calls.ProcessAsync(Request("dogmaIM", "Activate", module.Id, PropulsionWireDefinitions.EffectName, null, 0L));
            }
            Assert.NotNull(provider.GetRequiredService<PropulsionHandler>().ActiveEffect(context.Pilot, module.Id));
            Assert.DoesNotContain(packets.OfType<Eden.Protocol.Serialization.BlueInstance>(), p => p.Name.EndsWith(".ErrorResponse"));
            if (Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_OUTPUT") is { } destination)
            {
                Directory.CreateDirectory(destination);
                File.WriteAllLines(Path.Combine(destination, "slow-calls.log"), reports);
                File.WriteAllBytes(Path.Combine(destination, "dogma-payload.bin"), canonical);
            }
            foreach (string report in reports)
            {
                output.WriteLine(report);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static object? CanonicalDogma(object? value) =>
        value switch
        {
            IBlueWritable writable => CanonicalDogma(writable.ToBlue()),
            BlueInstance instance => new BlueInstance(instance.Name, CanonicalDogma(instance.State)),
            BlueConstructor constructor => new BlueConstructor(
                constructor.Name,
                constructor.Arguments.Select(CanonicalDogma).ToArray(),
                CanonicalDogma(constructor.State)
            ),
            BlueDictionary dictionary => CanonicalDictionary(dictionary.Entries),
            IDictionary dictionary => CanonicalDictionary(dictionary.Keys.Cast<object?>().Select(key => (key, dictionary[key!]))),
            object?[] tuple => tuple.Select(CanonicalDogma).ToArray(),
            List<object?> list => list.Select(CanonicalDogma).ToList(),
            _ => value,
        };

    private static BlueDictionary CanonicalDictionary(IEnumerable<(object? Key, object? Value)> entries) =>
        new(
            entries
                .Select(p => (Key: CanonicalDogma(p.Key), Value: CanonicalDogma(p.Value)))
                .OrderBy(p => Convert.ToHexString(BlueMarshal.Encode(p.Key)), StringComparer.Ordinal)
                .ToArray()
        );
}
