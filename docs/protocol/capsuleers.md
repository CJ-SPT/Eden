# Simulated capsuleers

[Documentation](../README.md) / [Protocol index](../protocol.md) / [Sandbox roadmap](../sandbox-vision.md)

## Persistent Kisogo miner

November 7, 2026. New economy worlds may opt into a seeded capsuleer miner. Schema 98 records the seed, career, school, pilot reference and active work. Ordinary character creation grants the starter hull, skills and civilian kit. The miner has a separate world-owned account, no launcher credential and a zero-balance wallet. The creation request survives the initialization transaction preceding character provisioning. The pilot, starting assets and population link commit together.

Build 3662168 identifies school 17, the Kisogo starting station 60015157 and the Ibis (type 601). Belt 40009051 contains Veldspar and Scordite in the pinned catalog. The miner chooses a known belt and searches for a visible rock on arrival. It fits and onlines its civilian laser, uses the catalog undock lane, aligns and warps, approaches the rock and takes the ship's targeting time before activation. Three completed cycles, a full hold or depletion triggers a return trip. It unloads ore through the ordinary inventory service. Missing equipment, an empty belt and rejected actions leave a recorded reason and a retry interval.

The server advances one world-owned miner on human-selected-pilot time. Progress uses active one-second ticks. Existing calendar work retains its UTC deadlines. Shared movement, target-lock calculations, capacitor payment, yield, inventory and docking rules apply. Each paid mining cycle records its next state in the same transaction as its payment or collection. Checkpoints capture immutable navigation, paid progress and relational state under the world owner. Projection and file writing happen on the detached image. A restart continues from the last completed checkpoint. The observer adapter supplies Local membership, ship contacts, warp orders and mining effects through existing client formats. The movement reference is [Carbon Destiny at 27ee40b](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ballpark.cpp#L2835-L2943).

The initial activity covers one miner operating in its school system. A station inventory accumulates its ore. Pirate hunting, economic decisions and inter-system trading are subsequent community milestones.

**Automated validation.** The solution restored and built with zero warnings and errors. The affected filter passed 130 tests in 21 seconds. Both Python policy suites passed (3 catalog and 9 formatting cases). Formatting checked 935 files and the 140-column limit. The sequence took 41.94 seconds. Documentation validation checked 449 local links and their anchors. The full .NET suite was not requested.

```text
FullyQualifiedName~Eden.Protocol.Tests.Capsuleers|FullyQualifiedName~CharacterCreationTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting|FullyQualifiedName~ChatWorldTests|FullyQualifiedName~ChatRuntimeTests|FullyQualifiedName~XmppSessionTests|FullyQualifiedName~Eden.Protocol.Tests.Mining.MiningTests|FullyQualifiedName~AsteroidFieldTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~WorldInitializationTests|FullyQualifiedName~LiveWorldTests|FullyQualifiedName~LiveSimulationTests|FullyQualifiedName~SystemMotionTests|FullyQualifiedName~WarpTests|FullyQualifiedName~LauncherStoreTests|FullyQualifiedName~EconomicLifecycleTests
```

The portable replay uses the Kisogo station and belt coordinates, racial mining skill, catalog Ibis and civilian module definitions, and an isolated Veldspar field. Two observers share one ship and mining effect. Local membership is checked before and after arrival. The laser pays once across a pause and reopen, the hold unloads at the school station, and the asteroid decrease balances the mined stack. The waiting tick issues zero SQL commands, connection opens and fit calculations. A second replay reopens during warp and rolls back a collection before retrying settlement. The checks establish simulation and protocol state; native movement rendering and combat encounters during the route remain unverified.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** In an isolated opt-in world, watch the miner's departure and laser effect from two clients in Kisogo.
