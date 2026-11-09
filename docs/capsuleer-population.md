# Capsuleer encounters

[Documentation index](README.md) / [Implementation evidence](protocol/capsuleers.md#galaxy-wide-station-and-gate-traffic)

## zKill-backed travelers

An optional local `traffic.sqlite` beside the world database supplies real pilot names, varied supported ships and complete victim fits. It is loaded at startup, and gameplay makes no network requests. An absent, incompatible or corrupt snapshot leaves generated Badger traffic available. Saved imported pilots keep their local identity, ship, equipment and losses even when the snapshot changes or disappears.

Create or refresh the snapshot from the repository root:

```powershell
dotnet run --project src/Eden.Server --configuration Release -- --import-traffic "$env:LOCALAPPDATA\Eden\traffic.sqlite"
```

The default window is the latest **seven published complete UTC days** in zKill's archive totals. Publication can lag the UTC day boundary. `--from yyyy-MM-dd` and `--through yyyy-MM-dd` select an explicit range; both dates are inclusive. `--catalog <path>` selects the pinned catalog, defaulting to `catalog.sqlite` beside the output. Restart the server separately to adopt the new snapshot. Importing does not open or alter a saved world and may run while gameplay continues using the previous snapshot.

Completed archives, resolved names and fitting-validation decisions are cached in `<output>.cache`. Downloaded archives retain their SHA-256 checksums in the snapshot manifest. Fit decisions are keyed by catalog fingerprint, validator assembly and equipment configuration; every ammunition quantity still receives an individual capacity check. Failed downloads, cancellation, unsupported-only samples and failed validation preserve the installed snapshot. The command refuses to replace a world or catalog database.

Selection prefers the route's region and security band, then the same security band elsewhere, then compatible global observations. Each pool samples unique characters using the saved world seed. The importer retains the newest supported observation for each character, hull, region and security band; older observations of that same combination cannot improve the available selection and are counted as superseded. Unsupported newer fits do not hide older supported observations. Each imported character is reserved once per world, including after loss or deletion. Conflicting local names are skipped without modifying either name.

Every selected hull must be published and admitted by build 3662168's existing support policy. A complete fit must reproduce every fitted module, rig, subsystem and loaded charge at its recorded slot. Unsupported subsystems and other equipment reject their entire record. Normal fitting rules check skills, slots, hardpoints, CPU, power, calibration, module limits and ammunition compatibility. Required skills and supported fitting/navigation skills are granted at level V. Every module must remain legally online; rigs apply their normal fitted effects. No equipment is substituted or omitted.

Imported travelers receive no starter ore or recorded cargo, drones, implants, corporations or alliances. Fitted passive effects alter ordinary ship statistics. Travelers retain existing travel and retreat behavior; they do not activate weapons or other combat equipment. True capitals cannot use routes entering high security, and supercapitals are excluded. Existing build support restrictions can narrow this pool further.

Schema 121 saves each imported pilot's source character, kill and snapshot identity. Untouched generated Badger traffic can convert once while docked and undamaged, preserving the pilot and ship IDs, wallet, owned cargo, route and saved timing. Customized names, altered equipment and lost ships are retained. A smaller hull is deferred when existing cargo cannot fit. Grants, conversion and source reservation share the owning transaction. Miners and traffic density remain unchanged.

See the [implementation and automated evidence](protocol/capsuleers.md#zkill-backed-traffic--november-8-2026).

## Galaxy-wide station and gate traffic

On the first human visit, Eden looks for real stations and usable reciprocal gates in the build-3662168 catalog. Routes connect real stations in one system or across reciprocal gates. Stationless transit systems can carry through-travelers. Stationless dead ends stay quiet. All security bands use the same traffic policy. Isolated systems need at least two usable stations for local traffic. Gate-free systems with no station pair stay quiet.

The following table gives the number of **home haulers** a system can acquire when a route exists. The first matching row applies. The saved seed chooses within each range. Miners and visiting haulers can increase the number of pilots seen in Local.

| System geography | Home haulers |
| --- | ---: |
| At least 6 gate connections or 8 stations | 5–6 |
| At least 4 gate connections or 4 stations | 3–4 |
| At least 3 gate connections or 3 stations | 2–3 |
| Two gate connections, or a gate-free system with a station pair | 1–2 |
| Other supported branches | 0–1 |

The first station approach in Jita, Amarr, Rens, Dodixie and Hek favors the established trade-hub undock when the catalog offers that route. [EVE University trade hubs](https://wiki.eveuniversity.org/Trade_Hubs)

Busy routes can first appear with a traveler inbound at a gate, another closing on a station, and others docked. Two observers share the same ships and Local membership.

### Destination journeys

Locals travel zero to two jumps from their saved home station and can return after docking. Regional visitors favor three to seven jumps. Long-distance visitors favor eight to twenty. A topology that cannot support the preferred distance uses a shorter compatible station route. Each new route has distinct station endpoints and visits each system once.

Home roster slots reserve recurring locals alongside regional and long-distance travelers. Busy station systems use a local/regional/long-distance cycle across their slots. Ordinary station systems reserve two local slots per three. Locals prefer trips between stations in their home system when available. Destination selection weights trade hubs and station clusters. The entire path must admit the selected hull, including restrictions on capitals entering high security.

A watched reciprocal gate pair introduces at most four durable visitors with staggered departures and distinct owned ships. The first visitor can already be finishing a moving approach on the unobserved source side. Remaining visitors wait docked at their real source stations and fly the full departure route. Gate arrivals use a stable spread around the arrival shell. Both ends share those identities. Watching the opposite side preserves each traveler's direction and deadline. Subsequent trips begin at the pilot's actual docked station and favor a destination other than the previous origin when alternatives exist. Visitors make an onward stop before routing home, keeping a continuing circuit. Homeward legs follow real compatible paths.

Established travelers continue on the shared human play clock through unobserved systems. Background alignment, analytic warp, approach, docking and gate-transfer deadlines retain the same navigation and ownership checks. Observed systems use ordinary flight samples. Empty worlds pause the shared clock. A witnessed departure completes once. Tackle, retreat, loss, cargo and equipment retain their normal effects.

Locals wait 135�165 seconds at stations. Visitors wait 615�645 seconds. First departures use the busy/quiet gate cadence to stagger the four visitors across successive intervals. The first arrival includes the actual station-to-gate flight. Waiting and navigation progress survive restarts.

Journey metadata lives in the existing version-125 hauler checkpoint payload. Existing pilots finish their current route to a safe dock before selecting a new destination. A roster's paused legacy travelers resume on system activation. Previously misassigned locals return to their community before beginning local trips. Legacy paused arrivals preserve their remaining delay when enrolled into continuous travel. Provisioning policy version 4 refreshes visited-system eligibility while retaining roster slots and assets.

The administration performance page reports the last complete traffic update, simulation/preparation and scene/Local publication separately. The complete update includes gate watching and presentation capture. Checkpoint capture, projection and writing remain separate measurements.

World schema 120 retains prior pilots in their original roster slots. Generated names get natural replacements and custom pilot or ship names are preserved. Missing roster members are granted on visits. A gate or station count determines traffic independently of mining eligibility, local ore availability and regional market activity.

See [destination-journey validation](protocol/capsuleers.md#destination-journeys--november-9-2026) and [historical traffic evidence](protocol/capsuleers.md#galaxy-wide-station-and-gate-traffic).

## Observed encounters — November 8, 2026

The encounter lifecycle supersedes the global autonomous economy below. First human entry prepares local supported mining and hauling encounters. New miners can start docked, returning from a deposit, or in a paid cycle at an actual deposit. New haulers receive a finite owned cargo once. Ordinary navigation, gate travel, damage, losses and immutable checkpoints remain authoritative while observed. Multiple observers share one scene and identity.

Unobserved pilots retain compact progress and owned assets. Their jobs remain dormant. Re-entry rebases remaining activity time without replaying the absence. A witnessed gate transfer can finish once after its source loses its observers. After fifteen human-active minutes, a familiar hauler can reappear at its other connected endpoint, selected deterministically by identity and active hour. Cargo and losses remain saved. Lost ships are never granted again. Entry dispatch has no job-draining barrier.

Startup no longer provisions every eligible catalog system or a first-human founder. Eligibility and new actors are prepared on visits. Existing founder identities, personal orders, assets and paid progress become remembered encounters. Native scenes and loaded role navigation are released when their system becomes unobserved. All saves adopt this lifecycle. The old global-provisioning switch no longer grants a global population.

The initial November 8 profile, superseded by the galaxy-wide traffic rules above, used the supported high-security Venture/Badger cohort and reciprocal station routes. Systems with three or more gate connections receive a hauler. Half of quieter systems receive a hauler, determined by the saved seed. A connected market system without belts can receive hauling independently of mining. Other systems can remain empty. Regional progressive markets now develop independently. Encounter mining and hauling never create or fill market orders.

See the [implementation and validation record](protocol/capsuleers.md#observed-capsuleer-encounters--november-8-2026).

## Previous global simulation

The following sections preserve the previous design and its measurements as historical evidence.

## Agreed outcome

Build a persistent population of autonomous economic pilots across New Eden. Start with one Venture miner and one Badger hauler per eligible system. Schema 118 provisions one pair per eligible system by default. The installed catalog measurement registered 8,490 systems, validated 902 eligible communities and created 1,804 pilots in an isolated world.

Eligibility requires supported high-security mining, a market station and a reciprocal route to another supported market. Unsupported systems receive explicit deferral reasons rather than fabricated jobs. The eventual system registry covers every catalog system.

Any selected human pilot activates their system, including while docked. Multiple human pilots keep a system active until the last one leaves. Other systems use background events with the same ownership, resources, costs, navigation and NPC danger rules. The universe advances economic activity only while at least one human pilot is selected; installed industry and training retain their existing deadline rules.

## Implementation increments

| Increment | Scope | State |
| --- | --- | --- |
| 1. Durable foundation | Register existing founders by source system, add saved jobs and bounded scheduling rules, track selected humans independently of chat, checkpoint a monotonic human-active clock. | Implemented in schema 116; validation is recorded in the protocol notes. |
| 2. Role execution | Generalize the miner and hauler into per-agent action handlers; migrate remaining paid/travel durations onto clock deadlines; commit feature mutations and job advancement together. | Implemented in schema 117; both founders now run through the shared job scheduler. |
| 3. Simulation handoff | Settle due work before commands and system entry, preserve encounter/projectile/effect state, materialize active systems and release inactive scenes. | Implemented: world-owned modes, command/entry barriers, background warp/gate events and native scene release. |
| 4. Global rollout | Register all catalog systems and reasons, resumably seed eligible communities, expose diagnostics, and measure the actual population and larger workloads. | Implemented in schema 118; eligibility, atomic grants, saved seeds and diagnostics. Actual-catalog measurement passed; final sustained/larger-load checks remain pending. |

This sequence keeps the existing miner and hauler available while the common runtime is introduced. Completing the foundation does not establish completion of the global-population milestone.

## Foundation contracts

`ICapsuleerPopulation` returns community and founder metadata without loading fits, navigation, NPC encounters or native scenes. Communities use their source system as the stable key. Founders reference ordinary saved pilot identities and record their original ship, module and wallet grant. The unique community/career key prevents duplicate founders. Migration records existing grants without issuing goods or funds, and leaves the authoritative miner/hauler progress in its existing saved record.

`IHumanPresence` holds a lease for each selected flight session. Selection registers docked and undocked pilots; system changes transfer the lease; logoff, disposal and disconnect remove it. Chat authentication, channel membership and chat transport disconnections do not control the activity clock. An explicit session update entry point supports simulated sessions and future coordination. Presence is an internal trusted host contract, not a client request.

The human-active clock accumulates monotonic elapsed time once while any selection exists. Overlapping sessions do not multiply it. It retains fractions between timestamp samples and stores whole .NET ticks. Startup begins with no selected humans and resumes the saved elapsed value without offline catch-up. The resident owner captures the clock and relational image together; projection and file writing happen outside the owner. Failed saves retain the current state for retry. Production durability follows the ordinary checkpoint interval and graceful shutdown policy. The maintenance loop admits due jobs against this clock. Delayed wakes drain due work in bounded turns, while an empty world pauses all role jobs.

`CapsuleerJobQueue` orders current jobs by due time, pilot ID and action sequence. It keeps one ordered entry per agent, replaces externally revised jobs in logarithmic time, and rejects stale revisions. A turn admits at most 64 actions with a 10 ms soft budget; an atomic action finishes before yielding. The host runner uses the defined 50 ms wake interval. Exceptions leave the failed action due. The turn result reports completed work, registered jobs, oldest due time and an overrun flag.

The job table stores the current action, due clock ticks, revision and action sequence. `CapsuleerPopulationStore.Settle` compares the expected identity and complete job version before invoking a feature mutation. The mutation and next job share one transaction. Duplicate callbacks do no work; rollback preserves both the old job and feature data. Callers use a fresh operation context after failure and update the resident queue only after commit. Mining and hauling now each have a saved job. Their role operations synchronize the resident expected revision and commit the next job with feature mutations in one explicit transaction. Ordinary navigation and nonfatal combat retain resident execution; their job/progress records project at checkpoints and mutation boundaries.

## Shared role execution

`ICapsuleerScheduler` owns the due queue under the resident owner and dispatches to workers keyed by pilot ID. Factories provide the existing mining and hauling rules to each worker; aggression uses a ship-to-pilot index. There is no separate role advancement API. Maintenance publishes committed miner/hauler batches through the existing native scene and chat adapters.

Schema 117 seeds missing jobs once and maps saved local phase deadlines onto the saved human-active clock. Local navigation stamps are derived from a saved clock offset. Paid laser cycles retain their funded work, and their native cooldown is rebased on resume without another capacitor payment. Warp navigation, jump progress, current ships, cargo, wallets and founder identities survive restart. Docked retries sleep until due. Subwarp navigation and danger retain one-second samples, with shorter locking/mining deadlines preserved. Background warp and gate handling and native scene lifetime are described below.

Each economic or travel action opens its world transaction lazily. The same inventory, fitting, market, docking, gate, mining and NPC-disengagement operations serve humans and jobs. Goods, payments, progress and next-job version commit together. Failed loading now rolls back its purchase too. Rejected gameplay actions persist a waiting state after rollback; unexpected failures leave the old action due for retry. Resident navigation, targets and positions restore after failed settlement. Checkpoints capture every worker's immutable progress and current job with the same world generation.

World access precedes `WorldDatabase.MotionLock` in NPC contacts, anomalies, electronic warfare, movement and translocation. The scheduler already owns the world before requesting these views. Reversing that order lets a human login/update and a bot hold one lock each and wait indefinitely. A live server stack captured during the reported login freeze established this inversion; automated contention regressions cover the competing read paths. An already-deadlocked server requires a restart after rebuilding.

## Active and background handoff

The world-owned `SystemSimulationRegistry` moves observed systems through Background, Activating, Active, Deactivating and back to Background. Any selected human, including a docked pilot, keeps their system active. Multiple leases share one activation generation; only the first arrival or last departure changes it. All systems begin unobserved after restart. Connection modes and presentation generations are transient; saved job revisions, deadlines and the existing checkpoint generation remain the durable replay guards. Startup now registers every catalog system, including explicit reasons for unsupported locations.

`ICapsuleerHandoff` settles relevant system work before selected-human service dispatch, initial snapshots, selections and system transfers. Account and roster dispatch do not advance autonomous jobs. System barriers include the community haulers whose saved source or destination is that system, including inbound gate transfers. Other systems retain their own overdue work for maintenance. Its asynchronous barrier captures one human-active deadline at entry, releases the owner and yields between bounded queue turns, then performs the synchronous command under the same owner as the final due check. Each system retains a monotonic settled boundary while the global maintenance watermark catches up. Later wall-clock advancement cannot extend that request indefinitely. Maintenance immediately yields into another bounded turn while overdue, returning to the normal wake interval once current. It never holds a monitor across a network write or an asynchronous yield. Already-due work can finish at the frozen clock boundary after disconnect; the clock does not advance while no human is selected. A failed entry retains the previous membership. A disconnect removes its lease even if a due action fails, keeping the clock paused and the failed action retryable.

Native flight motion and autonomous mutations enter the same `IWorldSynchronization` owner. The separate database motion gate retains owner-first ordering. This avoids a new scene/world inversion around login or snapshots. The factory acquires selection before loading its initial objects and releases the lease if construction fails. Transfers publish the settled destination before its native snapshot.

One set of canonical jobs, ships, inventory, funded cycles and combat records serves both modes. Background navigation reads immutable collision/route geometry and resource rows without calling the native system-scene loader. Background NPC checks use compact identity/position/warp records; native `NpcContact` construction remains an observer operation. Existing shared acquisition, weapon, missile, repair and electronic-warfare operations retain their authoritative state. Webs and warp disruption affect both autonomous careers through the shared tackle attributes.

Background warps schedule the first strict exit sample of the existing analytic `WarpCourse`, and gate delays schedule the saved transfer deadline. Entry during warp samples that original course into resident position/navigation and the ordinary checkpoint without advancing the arrival job. Alignment, subwarp collisions, locking, mining danger and threatened work conservatively retain one-second samples. This increment establishes the handoff; it does not claim a fully sparse combat scheduler or global-population capacity.

Presentation batches carry increasing revisions and system generations. Applying a newer batch prevents an older captured batch from rematerializing a departed system. Deactivation removes autonomous native contacts, movement orders and NPC observer caches, while retaining canonical encounter membership, targets, health, missiles and effect deadlines. Local chat and station guests continue to follow canonical membership in background systems. Activation reconstructs native warp/beam/health presentation from current authoritative progress. Checkpoints continue capturing canonical jobs, role progress, clock and combat state together; no second active/background state is merged or substituted.

## Global provisioning and diagnostics

Startup reconciles catalog eligibility before the host listens. The saved world seed and versioned policy determine stable unique names, valid school/bloodline/race origins and initial job deadlines spread over 30 active seconds. Each community has its own transaction: actual generated deposits, both pilots, starting skills, ordinary owned ships/equipment/wallets, fitted miner laser, founder records and jobs commit together. A failed second founder rolls the pair back; restart resumes missing pairs. Existing founders, ownership, cargo, routes, funded work and job deadlines are preserved. A depleted saved field does not receive new ore through provisioning.

The restart-only global-provisioning setting controls additional grants. Disabling it retains established pilots and jobs. Eligibility records cover disabled systems, unsupported security, missing markets/belts/usable ore, reciprocal routes, origins and equipment, with the catalog fingerprint and policy version saved for reconciliation. Runtime reconciliation is rejected while the resident world owns the save.

The local administration population page (`/population`) and authenticated `/api/admin/population` endpoint expose coverage and per-system reasons. Performance diagnostics report observed systems, jobs, backlog age, completed actions, waiting reasons, failures, turn duration and native pilot scenes. Routine performance reads use an immutable published snapshot. Static population summaries are warmed once and reused without SQL or fitting work. System-mode updates visit observed systems rather than rebuilding all 8,490 entries each wake. Resident cache allowances include the registered population, so ordinary visitor limits do not evict every autonomous pilot in turn.

NPC population invalidation is scoped to the affected system. Calm, stationary NPC mining fleets with funded beams sleep until their next collection or pickup boundary. Ore and standings changes invalidate that deadline; movement, alarms, hostile standings, hauling and incomplete mining work retain conservative polling. This preserves the existing shared mining and combat operations while avoiding a database poll for every calm fleet every second.

## Measurement limits and future work

The protocol record reports an actual-catalog diagnostic and the default synthetic regression, including initial preparation, warm observer SQL/connection/fitting counts, due-action work, memory, background scene count and separate checkpoint phases. The final sustained rerun and larger synthetic workload remain pending after the user requested an immediate push. These are automated isolated-world measurements, not native-client acceptance or a general combat capacity guarantee. Detailed subwarp and danger sampling remains; broader combat-heavy equivalence and fully sparse combat scheduling remain future work.

Gameplay references are [EVE University NPC mining operations](https://wiki.eveuniversity.org/NPC_Mining_Operations), [mining](https://wiki.eveuniversity.org/Mining) and [hauling](https://wiki.eveuniversity.org/Hauling). Build-specific mechanics and historical Carbon/native observations remain in the [capsuleer protocol records](protocol/capsuleers.md).

## Startup and database cost

Provisioning reuses complete eligibility records when the catalog source fingerprint, file revision, policy version and provisioning setting match. Missing founders are checked in bulk, and deferred depleted fields are reconsidered when saved ore becomes available. A catalog edit invalidates the eligibility cache even when it retains the original import fingerprint. Full eligibility scans batch system metadata and share the belt-count dataset. Saved-world station and hull checks use the catalog indexes.

NPC hydration batches rows, mining membership, incoming electronic warfare and damage contributions for missing or changed members of a system. Position-only changes invalidate position and acquisition reads without reloading ship health, energy, ammunition and contributions. Background miner presentation omits observer health loading. Mining computes cargo volume once within each operation and updates it after extraction.

Checkpoint capture compares resident capsuleer jobs and progress with versions already committed to the resident SQL image. Detached projection batches role updates and job upserts. A successful file save does not clear a difference from the resident SQL image, because subsequent checkpoints copy that same image. Failed transactions do not advance the projected versions.
