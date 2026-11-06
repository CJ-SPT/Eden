# Architecture and typed boundaries

[Documentation](../README.md) / [Protocol index](../protocol.md)

Named models, Gameplay boundaries, orchestration, persistence and architecture audits.

These records preserve milestone evidence and historical observations. Dates, schema versions, test results and live acceptance apply to the recorded change; they do not establish acceptance of later changes.

## Contents

- [Attribute-based DI acceptance](#attribute-based-di-acceptance)
- [Named protocol models: shared session plumbing](#named-protocol-models-shared-session-plumbing)
- [Named protocol models: inventory and docked services](#named-protocol-models-inventory-and-docked-services)
- [Named protocol models: flight and combat](#named-protocol-models-flight-and-combat)
- [Named protocol models: market, industry, skills and brain](#named-protocol-models-market-industry-skills-and-brain)
- [Named protocol models: final audit](#named-protocol-models-final-audit)
- [Gameplay boundary milestone 1](#gameplay-boundary-milestone-1)
- [Session orchestration milestone 2](#session-orchestration-milestone-2)
- [Navigation boundary milestone 3](#navigation-boundary-milestone-3)
- [Inventory boundary milestone 4](#inventory-boundary-milestone-4)
- [Manufacturing boundary milestone 5](#manufacturing-boundary-milestone-5)
- [Architecture audit milestone 1](#architecture-audit-milestone-1)
- [Architecture audit milestone 2](#architecture-audit-milestone-2)
- [Architecture audit milestone 3](#architecture-audit-milestone-3)
- [Architecture audit milestone 4](#architecture-audit-milestone-4)
- [Architecture audit milestone 5](#architecture-audit-milestone-5)
- [Architecture audit milestone 6](#architecture-audit-milestone-6)
- [Architecture audit milestone 7](#architecture-audit-milestone-7)
- [Architecture audit milestone 8](#architecture-audit-milestone-8)
- [Architecture audit milestone 9](#architecture-audit-milestone-9)
- [Architecture audit milestone 10](#architecture-audit-milestone-10)
- [Architecture audit milestone 11: wallet header duplication](#architecture-audit-milestone-11-wallet-header-duplication)
- [Architecture audit milestone 12](#architecture-audit-milestone-12)

## Attribute-based DI acceptance

**Automated checks.** With SDK 10.0.403, restore succeeded, the build produced zero warnings and errors, all 32 tests passed, and formatting verification passed. The production registrations share one SQLite world across pilot lookup and position writes and isolate sessions between connections. Loopback and packet tests cover admission after failed login, bound inventory calls, successful None replies, unsupported calls, and the order of flight notifications.

**Staged Windows client, build 3662168.** Selecting Eden Pilot and opening Inventory showed **Ibis (Ibis)**. After undocking, Overview listed Jita Trade Hub at 453 m. Double-clicking open space accelerated the ship to an observed 267 m/s. **Stop My Ship** brought the HUD to 0.0 m/s at 924 m. Restarting the client and server restored the stopped ship at 924 m. Selecting **Dock** on the station returned the Ibis to the Caldari Navy Assembly Plant, where Inventory again showed **Ibis (Ibis)**. After the second restart, the host loaded pilot `90000001` at station `60003760` in ship `1000000000001`, and the client showed the same active Ibis in Inventory.

## Named protocol models: shared session plumbing

The first typing milestone introduces `IBlueWritable` with explicit conversion to the existing inert Blue vocabulary, typed lists/maps, signed settings, Macho addresses, parsed call envelopes, account/login session models, and explicit `PilotCallResult` success/unsupported/failure/named-failure cases. Successful Python None is independent of unsupported calls. Bound-call failures still escape the binding response instead of becoming successful tuple contents. Packet forwarding and session-delta construction now have a dedicated adapter. Temporary legacy handler/reply and notification bridges keep subsequent feature migrations buildable; they are not the intended API for new code.

This preserves the native Blue forms established against [Carbon Blue af0d65b29f951260ea09140ca11adf5d40566997](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp). No schema, gameplay, wire vocabulary, constructor allowlist, or Destiny simulation changes are introduced. Contributor guidance now requires named models for known shapes and confines flexible values to explicit adapters and genuinely dynamic boundaries.

**Automated checks.** Tool restore, restore, build (zero warnings/errors), all **365 tests**, catalog setup's **3 tests**, formatting policy's **9 tests**, and `Format.ps1 -Check` passed. Five new tests compare explicit pre-refactor wire forms for settings, addresses, account/session dictionaries and typed collections, distinguish successful None and failures, and exercise malformed call envelopes. Existing bound-call error, overflow, selection-gating, and session-ordering coverage remains active.

**Native observation.** Launched staged build **3662168** with local login against an isolated copy of the previously verified fitting save (`Lab/typed-protocol-20261102`). The client authenticated and entered **Eden Pilot** at **Perimeter II - Moon 1 - Caldari Navy Assembly Plant**. The station view rendered the Ibis; Inventory displayed active **Ibis (Ibis)**, **Civilian Miner**, **Tritanium 100**, and **6.0/125.0 m³**. The normal save was not modified. Selection was supplied in the interactive client; this establishes successful login/session handling and restored inventory display, not acceptance of later migrations.

## Named protocol models: inventory and docked services

Inventory rows now expose their nine physical columns as named properties and share the existing virtual-column descriptor. Dogma item keys distinguish ordinary items from charge sublocations. Item information, active effects, ship instances, magazine quantities and the complete ship-state response are typed; boarding consumes `ShipState` directly. Training attributes use enum keys and integer values, preserving their existing wire representation. Cargo movement parses an `InventoryMoveRequest` before the gameplay operation. Inventory/blueprint, module-attribute, charge-loading, prime-item and online-effect notifications carry named payloads. Repair and reprocessing replies, character cards/public info, station/location/owner rows and the populated docked compatibility records have explicit adapters.

The existing wire assertions now materialize adapters in a test-only helper, preserving pre-encoding constructors and CLR numeric widths. Four new compatibility tests cover inventory headers/rows, integer dogma placeholders versus floating resources, timed/indefinite effects and charge keys, and old inventory column values. Gameplay assertions inspect typed charge-prime data and active-effect properties. No catalog/world migration or gameplay rules changed; the established Carbon Blue revision remains applicable.

**Automated checks.** Tool restore, restore, build with zero warnings/errors, and all **369 tests** passed. Catalog setup's **3 tests**, formatting policy's **9 tests**, and formatting verification passed.

**Native observation.** Restarted build 3662168 against the same isolated save. The character card showed **Eden Pilot**, **Ibis**, **384,297 SP**, and **Perimeter**. After interactive selection and undocking, Computer Use observed the ship in space and Inventory showing **Ibis (Ibis)**, **Tritanium 100**, **Civilian Miner**, and **6.0/125.0 m³**. Automated UI clicks were repeatedly declined with “user input was detected in this window”; selection/undocking were supplied interactively. Additional transfer, fitting, repair, and reprocessing click-through acceptance remains unverified; their wire and persistence regressions passed.

## Named protocol models: flight and combat

Flight queues `StampedDestinyUpdate` records with named movement, follow/orbit, state, addition/removal, health, slim-item and special-effect commands. `BallparkSnapshot` and `BallparkAddition` expose binary state, typed slims and typed damage maps. Static, ship, NPC and wreck slim adapters retain their prior optional fields and ordering. `ShipHealth.DamageState()` now returns `ShipDamageState`, preserving the nested shield tuple and floating recharge time constant. Combat messages, targeting changes/replies, mined-ore feedback and module-effect/resource notifications have named payloads. The session retains its before-flight and normal notification queues and their delivery order.

The binary ball writer and movement calculations are unchanged. Command field types follow [Carbon Destiny thunks at 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Thunkers.cpp). Four additional fixture tests compare damage tuples, stamped commands, integer warp factors, short/module/jump effect forms and full snapshot dictionaries. Existing trajectory, binary ball records, ship-loss and notification-order regressions remain active; the kill-order test now inspects typed commands directly.

**Automated checks.** Restore tools/solution, build with zero warnings/errors, all **373 tests**, catalog setup's **3 tests**, formatting policy's **9 tests**, and formatting verification passed.

**Native observation.** Restarted staged build 3662168 against the isolated typing save. Computer Use observed Eden Pilot in **Perimeter II - Asteroid Belt 1**, a locked **Guristas Rookie** at approximately **4,052 m**, the **Keeping at Range — Guristas Rookie — 1,000 m** indicator, **63.7 m/s** on the speed display, an ammunition icon with **107** rounds, and incoming combat text. The server logged `CmdGotoDirection`, `CmdFollowBall`, `dogmaIM.Activate`, `OnDamageMessage`, and continuing Destiny/module-attribute notifications without an error. Inventory continued to display the Ibis and original cargo/capacity. Interactive flight inputs were supplied while automated input was blocked by concurrent user activity; a complete agent-driven combat/loot/reconnect sequence remains unverified.

## Named protocol models: market, industry, skills and brain

Market order/history/cache replies, trades, wallet entry definitions and journal entries now have named models. The wallet aggregates and sorts `WalletJournalEntry.Time` before serialization; it no longer casts through encoded dictionaries to recover dates. Market sale/purchase adapters produce the existing typed transaction requests. Industry facility/blueprint/job/location replies and monitor/job notifications are typed, retaining the native `industry.job.Location` NEWOBJ form. Manufacturing validation still constructs the existing typed `ManufacturingRequest` before installation.

Skills use typed entries, queue/history rows, queue parsing and named notifications. `PilotBrainState` exposes version and character/ship/structure effect collections; `BrainEffect` names its modifier, attribute, operation, extras, skills and value. Gray matter encoding uses those properties. Inventory now carries the brain model directly. Blue NEWOBJ forms and the previously recorded Carbon revision/allowlists remain unchanged. Four new fixtures cover wallet amounts/filter boundaries, brain state, native manufacturing locations/material quantities and invalid queue positions.

**Automated checks.** Tool/solution restore, build with zero warnings/errors, all **377 tests**, catalog setup's **3 tests**, formatting policy's **9 tests**, and formatting verification passed. A pre-encoding assertion was updated to expect the journal's typed long reference ID; the wire bytes are unchanged.

**Native observation.** Restarted staged build 3662168 against the isolated save. Computer Use observed the Skills catalogue with **Shield Operation II** in **Training Queue 1/150**, approximately **1h 18m** remaining, and **384,298** total SP. A later Character Sheet showed **384,307** SP and the Perimeter home station. The server logged `SaveNewQueue`, `GetSkillQueueAndFreePoints` and `OnNewSkillQueueSaved`. Automated input remained declined because of concurrent interactive activity. Market purchase and manufacturing install/deliver click-throughs remain unverified; their automated wire/persistence tests passed.

## Named protocol models: final audit

All account and selected-pilot handlers, the router, module controllers and flight call entrypoints now return `PilotCallResult`. Successful None is explicit; success contains a `BlueModel`, never an arbitrary object. Primitive successes have private typed adapters. Native constructor/reference replies have explicit codec adapters. The temporary raw reply entrypoint, `FromLegacy`, `LegacyReply`, tuple-to-notification bridge and untyped notification implementation have been removed. The session consumes typed notifications and delegates response/error framing to its wire adapter. Login credentials/key exchange/proof and signed settings are typed. Pure inert-value validation now lives in `BlueRead`; `BlueRows` owns shared descriptor construction.

The remaining populated and structured compatibility replies have named models, including owner/ticker tables, bounties, contacts, clone/crimewatch states and personal bookmark state. Empty-only compatibility records describe the currently implemented defaults and do not claim schemas or functionality for missing features. Python empty lists, tuples and dictionaries retain distinct explicit representations.

Reviewed all remaining production `object` matches. Comments and diagnostic strings are not type declarations. Intentional type uses fall into these boundaries:

| Boundary | Retained flexible values and reason |
| --- | --- |
| Blue serialization | `BlueMarshal`, inert Blue instances/constructors/packed rows/dictionaries, `BlueRead`, and planet-file parsing operate on heterogeneous Python graphs, shared references and client-supplied row schemas. The constructor/global allowlists and inert-data policy remain unchanged. |
| Outbound adapters | `BlueModel.ToBlue()` / `IBlueWritable`, native row builders, and feature wire/reply/notification records create heterogeneous native tuples, lists, maps and class state. Their stored fields and caller-facing properties are typed. Generic scalar adapters only have explicit primitive entrypoints; generic container boxing is confined to conversion. |
| Service dispatch and binding | Handler `Call` methods, `PilotCallRequest`, router/binding validation, `FlightSession.Call`/`StartJump`, and request parsers inspect raw arguments/keywords because the service/method determines their schema. They validate and convert to named requests or typed operation parameters. Opaque incoming MachoNet routing/forwarding fields remain inside `IncomingMachoPacket`; session logic no longer indexes packet arrays. |
| Transport | `IPilotSession` read/write delegates and `EncryptedPackets` carry the codec's heterogeneous values. The signed-settings parameter and stored call-context settings are now `SignedSettings`. |
| Appearance | Character-creation validators inspect client-defined appearance graphs. Appearance/portrait replies retain validated persisted bytes and decode only in the adapter. No speculative appearance schema or executable Python object is introduced. |
| Server/framework boundaries | The catalog export writes the required heterogeneous JSON `[planetID, attributeArray]` pair. The console cancellation callback retains the framework's `object? sender` signature. Persistence and catalog gameplay models have no object-valued state. |

The README and contributor guide document this policy for new code, including explicit successful None, typed notifications/commands, and reuse of shared wire helpers. Public C# changes include typed signed settings and `ShipDamageState`; there is no database migration, gameplay-policy change, new wire vocabulary or reflection-based field serialization. Existing Carbon Blue **af0d65b29f951260ea09140ca11adf5d40566997** and Destiny **27ee40b38a563d29054b32f64c3ebc87fabaec1f** references remain applicable.

**Final automated verification.** Tool restore, solution restore, build with **zero warnings/errors**, all **382 tests**, catalog setup's **3 tests**, formatting policy's **9 tests**, and `Format.ps1 -Check` passed. Five final tests exercise typed false/integer-zero/floating-zero/None outcomes, selection gating and failed nested bindings, eleven structured compatibility defaults and distinct empty container kinds, named error values, and malformed login inputs. The existing wire, transport, gameplay, persistence, overflow, ownership, notification-order and migration tests remain active.

**Final native observation and limits.** Launched staged build **3662168** against the isolated typing save after removing the bridges. Computer Use observed **Eden Pilot** at **Perimeter II - Moon 1 - Caldari Navy Assembly Plant**, active **Ibis (Ibis)** in Inventory, and the Ibis fitting window with populated high/mid slots and ship statistics: **149.0 GJ** capacitor, **3.9 dps**, **833 ehp**, **339.3 m/s**, **52.0/162.0 CPU**, **11.6/27.6 power grid**, and **6.0/125.0 m³** cargo. The server confirmed pilot **90000001**, station **60003754**, ship **2000000000023**, and remained running without a stderr exception. This is the evolving copied test save, not the normal save. Earlier milestone observations separately cover flight/combat and the active skill queue. Automated UI actions were blocked by concurrent interactive input; transfer/slot-move, repair/reprocessing, market purchase and manufacturing install/deliver click-throughs, and a complete combat/loot/reconnect sequence remain unverified. Automated regressions for these services passed. The client and server were left open on the copied test save.

## Gameplay boundary milestone 1

On 2026-11-03, the architecture refactor began from clean revision f52b597. An isolated baseline build passed with zero warnings and all 382 .NET tests passed. Catalog setup passed 3 Python tests and formatting policy passed 9. The initial build encountered the running server's locked binaries, so baseline output was isolated. Baseline formatting overlapped subsequent source edits and is not counted as a clean baseline result.

Eden.Gameplay owns combat damage arithmetic, health state and its persistence contract, recharge calculations, coordinates and catalog attribute IDs. Eden.Protocol converts health into the native damage-state model. Eden.Server discovers Gameplay contracts on its existing attributed implementations. Health stores retain their transient lifetime. Gameplay's build rejects package and project references, and an assembly dependency test verifies runtime-only references.

Automated acceptance passed restore, build with zero warnings, 383 .NET tests, 3 catalog setup Python tests, 9 formatting policy Python tests and formatting verification. Existing health tests check layered damage, recharge, concurrent hits, restart persistence and the exact native damage-state snapshot.

Live acceptance used staged build 3662168 and a separate copy of the prior test save under `%LOCALAPPDATA%/Eden/Lab/architecture-20261102`. Eden Pilot entered Perimeter II - Asteroid Belt 1. Inventory showed Ibis (Ibis), 100 Tritanium and a Civilian Miner with 6.0/125.0 cubic metres used. The HUD rendered health rings. Activating Small Shield Booster I displayed its 26 HP per 2 seconds tooltip and produced native effect and attribute updates. Shields were already full. This checks presentation and activation, while damaged-health arithmetic is covered by automated tests. The copied save and client remain available for subsequent milestones.

## Session orchestration milestone 2

On 2026-11-03, PilotServices delegated flight construction to FlightSessionFactory, common feature advancement to the connection-scoped PilotUpdateCoordinator, bound-reference storage to PilotBindings, and socket output to PilotSessionOutput. Timer-only skill and flight checkpoints remain explicit. Both advancement paths retain targeting, mining, combat, shields, propulsion, industry, NPC combat and targeting in their original order. The coordinator accepts account calls before selection. Ship-loss transitions still precede rejected-call replies, and the before-flight queue still precedes Destiny updates.

Automated acceptance passed restore, build with zero warnings, all 383 .NET tests, both Python suites (3 and 9 tests) and formatting verification. Existing session tests cover account calls before selection, bound inventory routing, notification ordering, rejected calls and recovery hull announcements.

Live acceptance restarted the staged client against the milestone build and the isolated architecture save. Eden Pilot re-entered the belt with Ibis inventory and health rings visible. Double-clicking open space issued CmdGotoDirection. The ship turned, the speed display reached 133 m/s, and wreck ranges changed during timer updates without further calls.

## Navigation boundary milestone 3

On 2026-11-03, FlightRequestWire became the decoding boundary for undock, heading, speed, follow, orbit, warp, docking and gate commands. FlightSession now accepts named FlightCommand records. Gameplay owns NavigationState, typed NavigationTick and NavigationChange results, orbit and warp courses, collision integration, warp-in calculations, static object models and the narrow system/position interfaces. FlightSession retains the connection's single navigation instance and translates its tick changes into DestinyCommand records. Visibility comparison, peer reconciliation and client snapshots remain in Protocol. Immutable ShipContact samples still cross connection boundaries through SharedSpace.

The extracted formulas retain the existing Carbon Destiny reference at revision 27ee40b38a563d29054b32f64c3ebc87fabaec1f, including alignment, warp acceleration and deceleration, old-style orbit and collision integration. See the source links in the flight sections above. Existing flight tests now pass raw test calls through the same request adapter used by production handlers.

Automated acceptance passed restore, build with zero warnings, all 383 .NET tests, 3 catalog setup Python tests, 9 formatting policy Python tests and formatting verification. Coverage includes invalid headings/ranges, warp state, gate transitions, collision/approach, orbit, peer snapshots and native Destiny arguments.

Live acceptance used both staged clients on the isolated architecture save. Eden Pilot warped from Perimeter II belt 1 to the Jita gate, played the gate flash and tunnel, and arrived in Jita beside the Perimeter gate. The server recorded transfer from system 30000144 to 30000142. Eden Wing undocked from Jita IV - Moon 4. Eden Pilot warped to that station and coasted out of warp. Pilot's Overview showed Eden Wing in an Ibis at 183 km, and Wing's Overview showed Eden Pilot in an Ibis at the same distance.

## Inventory boundary milestone 4

On 2026-11-03, Gameplay became the owner of inventory changes, fitting and ammunition contracts, magazine state and typed inventory moves. Native row-column indices, singleton quantities, effect names and notification construction remain in Protocol. InventoryChangesWire owns shared item-change and blueprint-cache notifications. MagazineWire owns charge priming and quantity/reload notifications. Inventory, ammunition, mining, industry, market, skills and loot use these adapters. Inventory reads active cycle effects through IModuleEffectSource, with the original connection-scoped feature handlers remaining their state owners. Hosting tests verify that this interface resolves the same scoped mining handler.

Transfers select named move, fitting and wreck-looting paths. The common post-transfer sequence still clears old magazine quantities, detaches moving modules, sends changed item rows, and primes charges after the client fetches the new module slots.

Automated acceptance passed restore, build with zero warnings, all 383 .NET tests, both Python suites (3 and 9 tests), and formatting verification. Existing loaded-ammunition and fitting tests continue to exercise transfer identities, slot swaps, charge notifications and reload state.

Live observation showed Eden Pilot docked in Jita with Inventory showing the active Ibis and Fitting showing the miner and loaded railgun. The maintainer took control of the game for this check and confirmed that swapping the miner and loaded railgun preserved the 76 Iron Charge S rounds and showed both modules in the new slots after reopening Fitting. The maintainer also observed that dragging the loaded gun onto the miner does not swap modules. The pinned client drags the gun's ammunition from that icon, as already documented in the fitting guide. Dragging the miner onto the gun performs either swap. This is maintainer-reported acceptance, alongside the agent's visible Inventory/Fitting observations.

## Manufacturing boundary milestone 5

On 2026-11-03, Gameplay became the owner of the industry request, job and persistence contracts and the pure ManufacturingRules operation. Server loads the recipe, skill bonuses and active-job count within the existing installation transaction. ManufacturingRules produces a typed ManufacturingPlan with duration and materials, checking slots, run-length limits, rounding and stale time/material quotes. Server consumes the materials, reserves the blueprint and creates the job within that same transaction. Protocol retains the client reply and notification adapters. This pattern is described in the contributor guide and README.

Automated acceptance passed restore, build with zero warnings, all 387 .NET tests, 3 catalog setup Python tests, 9 formatting policy Python tests and formatting verification. Four new pure test cases cover a one-run Iron Charge S recipe, midpoint-to-even duration rounding, stale quotes and occupied slots. Existing SQLite tests cover rollback of earlier stack consumption and blueprint changes, paid jobs across restart and delivery/cancellation.

The final live acceptance uses a separate consistent backup under `%LOCALAPPDATA%/Eden/Lab/architecture-manufacturing-20261103`, with an Iron Charge S original blueprint, 27 Tritanium and 21 Pyerite added to Eden Pilot's Jita Item Hangar as test supplies. The prior architecture save is separate. The maintainer installed a one-run job through the staged client. The server saved job 3 with a 288-second duration and a 100-round output. After restarting the host and client on that same save, the agent observed Industry showing the active job with about four minutes remaining and the manufacturing slot occupied. The saved deadline was unchanged. At completion, the client displayed Job ready for delivery. The maintainer delivered the job and confirmed 100 rounds. The agent then observed Industry showing Succeeded, the manufacturing slot free, and Item Hangar showing 100 Iron Charge S rounds. Read-only persistence inspection confirmed job status 101 and the 100-round station stack.

## Architecture audit milestone 1

On 2026-11-03, reprocessing capacity now credits only ore consumed from the output cargo hold. Separate mining-hold ore cannot provide cargo space. Three regressions cover full cargo with cargo, mining-hold and mixed inputs, including complete rollback. Both overflow regressions failed before the fix. Restore, build with zero warnings/errors, all 390 .NET tests, 3 catalog Python tests, 9 formatting Python tests and formatting verification passed. Build output is isolated under bin/Audit while the previous staged host remains separately installed.

Live acceptance used the Computer Use skill, build 3662168 and a consistent copied save at `%LOCALAPPDATA%/Eden/Lab/architecture-audit-20261103`. The fixture changes Eden Pilot's active hull to Audit Venture, fills cargo with 5,000 Tritanium and places 100 Scordite in its mining hold. Inventory showed the Venture and 15/5,000 cubic metres in Mining Hold. Reprocessing preview showed 75 Tritanium and 55 Pyerite. Reprocess to Item Hangar emptied the ore hold and produced the mineral output. The native output selector exposes Input Location and Item Hangar for this stack, so the explicit cargo-output overflow rejection is verified by automated store tests rather than a visible client request.

## Architecture audit milestone 2

Character appearance and portrait validation now bounds expanded traversal before serialization or pilot creation. AvatarWire charges 128 bytes of structural allowance per visited occurrence plus scalar byte content against a local 1 MiB budget, and caps nesting at 32. Shared subgraphs are charged each time they will be serialized. The budget is a local storage policy, independent of Carbon Blue's shared-reference wire encoding, and deliberately allows less than 1 MiB of actual serialized output. Known paper-doll rows and valid shared values preserve their original wire forms. The unbounded low-level Blue encoder remains suitable for trusted outbound models.

Four new tests cover ordinary appearance rows/shared fields, expanded shared appearance, expanded shared portrait and oversized byte content. Oversized creation calls fail through the production router before adding any pilot or ship. Existing encrypted-session character creation and appearance retrieval tests remain active. These hostile values cannot be constructed by the native character editor, so rejection is verified automatically. The staged-client reprocessing check from milestone 1 provides the live baseline, with no character-edit acceptance claimed here. Reference reviewed: [Carbon Blue af0d65b](https://github.com/carbonengine/blue/blob/af0d65b29f951260ea09140ca11adf5d40566997/src/Marshal.cpp).

Automated acceptance passed restore, build with zero warnings/errors, all 394 .NET tests, 3 catalog Python tests, 9 formatting Python tests and formatting verification.

## Architecture audit milestone 3

Blue byte-value reads now require the complete payload inside the value section, before the trailing shared-reference table. A truncated one-byte string is rejected instead of becoming an empty byte string. The regression failed on the previous decoder. Valid empty strings, one-byte strings and shared one-byte strings retain distinct forms. The reference is Carbon Blue af0d65b29f951260ea09140ca11adf5d40566997, Marshal::ReadObjectStrChar, which requires its data byte. These malformed streams cannot be generated by the native client UI and are covered directly in codec tests.

Automated acceptance passed restore, build with zero warnings/errors, all 399 .NET tests, both Python suites (3 and 9 tests) and formatting verification.

## Architecture audit milestone 4

ConnectionRunner owns both account admission and the client scope, disposing the scope before releasing the account. Scope construction, execution and disposal failures are contained and reported per connection. The listener cancels active connections on shutdown or accept-loop failure. Two new hosting regressions deliberately block cleanup and fail the second scope while another socket remains healthy and a third receives its greeting.

Bindings record account, pilot or location lifetime at creation. Inventory containers and wreck bindings expire with location bindings, ordinary boarding retires old container references, and the just-destroyed hull compatibility read remains valid until departure. Tests cover synthetic hangar/ship expiry and pilot-level skill binding retention. Targeting matches valid method/argument shapes before advancing anything, with a regression checking position, notification queues and pending lock state on declined calls.

PilotServices delegates timer advancement to PilotSessionTick and incoming packets to PilotSessionCalls. Feature advancement and before-flight ordering stay explicit. Rejected calls flush notifications produced by feature advancement after the error reply, including when no later request arrives. A dedicated packet test checks both queues are drained.

Live acceptance restarted the staged host/client on the isolated audit save. Eden Pilot entered Jita in Audit Venture and Inventory retained the manufactured charges and reprocessed minerals. Undocking rebuilt the space view and active-ship inventory showed 5,000 Tritanium using 50.0/50.0 cubic metres. The ship responded to heading input and reached 325 m/s while the station range changed. Deliberately blocked cleanup and connection failure isolation are automated observations, not live UI observations.

Automated acceptance passed restore, build with zero warnings/errors, all 405 .NET tests, both Python suites (3 and 9 tests) and formatting verification. Native reconnect, undock, Inventory and timer-driven motion were observed on the Session build. The final additional rejected-call queue test ran on the SessionVerified build.

## Architecture audit milestone 5

Gameplay now owns skill levels, training/book/read contracts, queue planning and character-name/origin rules. TrainingRules receives named skill inputs, projects prerequisites in queue order and retains a running head's deadline. Server persists progress, queue rows and events transactionally. Skill level lookups in fitting, manufacturing, tax and reprocessing reuse the existing transaction-aware reader. Native name-validation codes stay in CharacterNameWire. StartingSkills owns the catalog grant shared by ordinary creation and historical migrations. Appearance remains opaque persisted bytes.

Automated acceptance passed restore, build with zero warnings/errors, all 409 .NET tests, both Python suites (3 and 9 tests) and formatting verification. New pure tests cover threshold rounding and projected prerequisites with a retained running head. Existing school, name, creation rollback, queue, restart, skillbook and pilot-brain coverage passed.

Live acceptance restarted host/client on the same audit save using the Skills build. Eden Pilot's Venture and full 50.0/50.0 cargo were retained. Character Sheet showed the Jita home station and 385,224 total skill points. Skill Catalogue loaded the saved Shield Operation II queue with about 15 minutes remaining. Pausing changed Training Time to Paused while retaining the queued level and earned points. Resuming restarted the countdown. No new pilot was created during this UI check; school/name/origin and atomic grants are covered by automated tests.

## Architecture audit milestone 6

Mining, fitted-module, health, capacitor, player combat, NPC combat, shield-boost and propulsion operations now reuse the owning world's transaction and catalog. Module attribute calculations expose context-aware internal entrypoints; public reads own a consistent read transaction. Inventory rows and fitting resource reads also accept the current contexts, removing independent reader contexts inside writes. Signature-radius catalog defaults remain in the shared catalog reader. Health resolves the active hull and its defense inside one read transaction. NPC loot sampling and all durable writes retain their prior transaction owners.

A regression changes a tracked mining skill inside an uncommitted transaction, checks that authoritative module attributes see it, rolls back and checks that a subsequent public read retains the old value. Existing concurrent health/hits, skill modifiers, resource rollback, reload, fitting and hull-loss tests remain active.

Automated acceptance passed restore, build with zero warnings/errors, all 410 .NET tests, both Python suites (3 and 9 tests) and formatting verification. Live acceptance used a separate consistent manufacturing-save backup at %LOCALAPPDATA%/Eden/Lab/architecture-audit-flight-20261103 with the Transactions build. Eden Pilot entered Jita and Inventory showed Ibis (Ibis), 100 manufactured charges and the original blueprint. Fitting showed the loaded gun, miner, shield booster and afterburner, 3.9 dps, 149 GJ capacitor and 339.3 m/s maximum speed. Putting the gun offline changed offense to 0.0 dps and released its fitting resources. Putting it online restored the active icon and offense. Mining yields, paid combat/shield/propulsion cycles and concurrent-hull consistency have automated coverage; no belt combat was performed for this milestone.

## Architecture audit milestone 7

Gameplay now owns fitting modifiers/resource totals, target limits and lock-time arithmetic, mining module modifiers and collection amounts, weapon skill/hull/charge modifiers, afterburner mass/speed and module modifiers, and shield restoration/capacitor bonuses. Server loads typed inputs in the existing transaction and uses the same rules for authoritative actions and module attribute reads. Protocol translates weapon effect strings into WeaponActivation and retains effect names/graphics/binding prefixes in explicit wire definitions. Gameplay also owns the remaining mining, propulsion, repair, market, reprocessing, fleet, encounter and wreck persistence contracts. Its runtime-only dependency guard still passes.

Automated acceptance passed restore, build with zero warnings/errors, all 410 .NET tests, both Python suites (3 and 9 tests) and formatting verification. Existing afterburner tests caught a missing parenthesis during extraction; the corrected rule retains multiplicative catalog percentages, 8.5-second cycles and existing capacitor costs. Weapon, mining, fitting, targeting, cooldown, loot and health regressions all passed.

Live acceptance restarted build 3662168 against the ModuleRules host and the audit-flight save. Eden Pilot's Inventory showed the Ibis, 100 Iron Charge S and the blueprint. Opening Fitting rendered the loaded railgun, miner, afterburner and shield booster, 3.9 dps, 149 GJ capacitor, 13 hp/s shield repair, 29.70 km targeting and 339.3 m/s navigation, matching the previous transaction build. Paid module cycles and belt combat were verified by automated tests; no new belt fight is claimed for this milestone.

## Architecture audit milestone 8

Inventory dispatch now delegates transfers, online/offline operations, dogma projections, and deferred charge priming to separate components. A narrow Gameplay ship-resource reader returns the requested active hull's health, capacitor, and attributes from one transaction. AllInfo reuses that snapshot; boarding requests the ship-state projection directly. Inventory moves retain their typed request across the persistence boundary. Overlapping refits accumulate module acknowledgements and read current magazines before priming, preventing stale quantities; boarding clears pending primes.

Automated: restore, build, all 412 .NET tests, the 3 catalog Python tests, 9 formatting-policy Python tests, and formatting verification passed. Existing loaded-slot swap and capsule tests remain green; new overlap tests verify waiting and cancellation.

Live: restarted the InventoryBoundary host on the isolated audit-flight save, selected Eden Pilot, and inspected Inventory. The active ship was Ibis; Item hangar displayed 100 Iron Charge S, its blueprint, and five Civilian Miners. Native login/ship dogma priming completed. Loaded overlapping swaps and boarding cancellation were verified automatically, not manually. While navigating the toolbar, the unrelated unsupported map and enlistment calls displayed their existing error dialogs; no acceptance claim is made for those services.

## Architecture audit milestone 9

Reprocessing quote arithmetic, market fill/tax calculations, and partial repair budgeting/allocation now live in Gameplay. Server retains eligibility checks, catalog loading, and each atomic payment/item/order/health transaction. Station capabilities share one catalog lookup. A separate wallet handler depends on a narrow journal reader that reads trades, bounty rewards, and repair receipts in one transaction.

InventoryPersistence now owns owned-stack lookup, hold-volume measurement, checked ordinary-output merging and shared item/ship identity allocation. Market, manufacturing, mining and reprocessing use the shared output operation; cargo, ammunition and wreck capacity checks share volume reads. Magazine and wreck identity policies remain explicit at their call sites. The allocator includes unsaved tracked identities, and capacity reads include staged changes in the same transaction.

Automated: restore/build, all 413 .NET tests, 3 catalog Python tests, 9 formatting-policy Python tests, and formatting verification passed. The new transaction test exercises unsaved output merging, capacity, ownership, identity uniqueness, and overflow without mutation. Existing reprocessing, market, manufacturing, ammunition, loot and partial repair tests pass.

Live: restarted StationRules on the isolated audit-flight save. Selected Eden Pilot; Inventory showed Ibis and 100 Iron Charge S. Opened Wallet: balance 22,686 ISK, saved market purchases and bounty prizes appeared in Transactions. New paid trades, repair payments and reprocessing were covered automatically; they were not repeated manually in this milestone.

## Architecture audit milestone 10

NavigationState setters are private; Gameplay owns named docking, alignment-cancellation and hull-adoption transitions. DockedPilot is composed of named identity, ship, home station and current-location records. Peer samples use a separate observer projection with no account/profile object. Unsupported-call checkpoint behavior belongs to the Protocol request adapter. Contact creation is explicit and a snapshot publishes and serializes the same health sample.

FlightEffects owns the ordered mining, weapon, shield and propulsion replay used for arrival, refresh and snapshots. NPC disengagement runs on transitions rather than every docked call. ShipLossCoordinator preserves cancellation, wreck preparation and capsule adoption order. Gameplay owns typed NPC pursuit/orbit/return integration; Server retains shared wall-clock boundaries and MotionAt writes in the existing transaction. Follow/GotoThrust behavior was checked against Carbon Destiny revision `27ee40b38a563d29054b32f64c3ebc87fabaec1f` (`src/Ballpark.cpp`). The unused DistanceToStation helper was removed.

Automated: restore/build, all 416 .NET tests, 3 catalog Python tests, 9 formatting-policy Python tests and formatting verification passed. New tests cover single health sampling, production flight-factory wiring, docked disengagement and effect replay order. Existing two-pilot warp/gate/orbit, NPC competing-viewer movement and hull-loss/module cancellation regressions pass.

Live: restarted FlightBoundary on the isolated audit-flight save. Eden Pilot undocked in Ibis at Jita IV Moon 4. Launched Eden Wing afterward and undocked it; Wing's Overview displayed Eden Pilot's Ibis at 1,406 m, and its own HUD was at 0.0 m/s. This verifies late peer arrival through the production client. Warp, gates and fatal combat were covered automatically, not repeated manually. An unrelated native wallet initialization problem became visible during acceptance: the unsupported PLEX balance response can re-enter PersonalWalletPanel.Load and duplicate LP/EverMarks summary tabs; it is being investigated separately.

## Architecture audit milestone 11: wallet header duplication

The pinned client's PersonalWalletPanel.Load checks isInitialized before constructing tabs but only sets it afterward. PlexTab starts an asynchronous balance read during construction; its failure signal selects the ISK tab and can re-enter Load before initialization completes, appending the LP/EverMarks tabs again. The local gateway's 501 for eve_public.plex.vault.api.BalanceRequest triggered that path. This was visible during undocking and also reported by the maintainer's screenshot.

Added the native BalanceResponse and Currency.total_in_cents (sint64) shapes from build 3662168's eve_public/plex/vault/api/requests.proto and eve_public/plex/plex.proto descriptors. Local accounts have no PLEX holdings; a successful explicit zero balance avoids the failure callback. No PLEX purchasing service or currency grants are added.

Automated: restore/build, 417 .NET tests, 3 catalog Python tests, 9 formatting-policy Python tests and formatting verification passed. The regression verifies response identity, native type URL, explicit Currency presence and exact empty-balance bytes.

Live: restarted WalletBalance with the isolated audit-flight save, selected Eden Pilot in space and brought Wallet forward. Exactly one LP Corporations row and one EverMarks row appeared. PLEX displayed 0; the existing 22,686 ISK balance and market/bounty journal entries remained visible. Inventory showed active Ibis with 100 Tritanium and one Civilian Miner in cargo.

## Architecture audit milestone 12

World initialization is explicit: Program calls WorldInitialization.Initialize before starting the gateway and session listener. SqliteWorld construction only captures its database configuration. Fixtures initialize deliberately, and restart/migration probes call WorldInitialization.Open. A regression verifies that resolving the reader creates no world file and that explicit initialization is idempotent.

The shared TestClock replaces 16 duplicated feature clocks and offers independent wall-clock and elapsed-time advancement while retaining paired advancement where existing cycle tests require it. A regression verifies both independent controls. Production FlightSessionFactory integration coverage was added in milestone 10. SqliteWorld's authored dogma lookups now use named attributes, including the pinned catalog's baseWarpSpeed (1281) and warpSpeedMultiplier (600). README describes the resulting ownership and startup/fixture conventions.

Automated final baseline: pinned tool restore, solution restore, build with zero warnings/errors, all **419 .NET tests**, **3 catalog Python tests**, **9 formatting-policy Python tests**, and formatting verification passed. Formatting covers 267 authored files; git diff whitespace validation passed.

Live final host: launched ArchitectureFinal against the isolated audit-flight world through its normal entry point. It initialized successfully, listened on loopback and accepted the pinned client's encrypted login. The connected Eden Pilot window showed the Ibis docked at Jita IV Moon 4. Inventory retained 100 Iron Charge S and its original blueprint in Item hangar. Wallet showed the existing 22,686 ISK balance and saved journal, PLEX 0, and one LP Corporations row plus one EverMarks row. The final client was left open on that copied save. Automated checks and the narrower live actions recorded above remain distinct: the complete warp/gate/fatal-combat matrix was not manually repeated for this refactor.
