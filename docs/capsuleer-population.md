# Global capsuleer population

[Documentation index](README.md) / [Implementation evidence](protocol/capsuleers.md#population-and-scheduling-foundation--november-8-2026)

## Agreed outcome

Build a persistent population of autonomous economic pilots across New Eden. Start with one Venture miner and one Badger hauler per eligible system. The planning scan found approximately 902 candidate systems across 23 regions, or 1,804 pilots, before validating generated ore. This is a planning estimate, not an implemented population count.

Eligibility requires supported high-security mining, a market station and a reciprocal route to another supported market. Unsupported systems receive explicit deferral reasons rather than fabricated jobs. The eventual system registry covers every catalog system.

Any selected human pilot activates their system, including while docked. Multiple human pilots keep a system active until the last one leaves. Other systems use background events with the same ownership, resources, costs, navigation and NPC danger rules. The universe advances economic activity only while at least one human pilot is selected; installed industry and training retain their existing deadline rules.

## Implementation increments

| Increment | Scope | State |
| --- | --- | --- |
| 1. Durable foundation | Register existing founders by source system, add saved jobs and bounded scheduling rules, track selected humans independently of chat, checkpoint a monotonic human-active clock. | Implemented in schema 116; validation is recorded in the protocol notes. |
| 2. Role execution | Generalize the miner and hauler into per-agent action handlers; migrate remaining paid/travel durations onto clock deadlines; commit feature mutations and job advancement together. | Pending. Current role execution still uses the two legacy state machines. |
| 3. Simulation handoff | Settle due work before commands and system entry, preserve encounter/projectile/effect state, materialize active systems and release inactive scenes. | Pending. Presence tracks the activation set but does not perform scene handoffs. |
| 4. Global rollout | Register all catalog systems and reasons, resumably seed eligible communities, expose diagnostics, and measure the actual population and larger workloads. | Pending. Global founder grants are not enabled. |

This sequence keeps the existing miner and hauler available while the common runtime is introduced. Completing the foundation does not establish completion of the global-population milestone.

## Foundation contracts

`ICapsuleerPopulation` returns community and founder metadata without loading fits, navigation, NPC encounters or native scenes. Communities use their source system as the stable key. Founders reference ordinary saved pilot identities and record their original ship, module and wallet grant. The unique community/career key prevents duplicate founders. Migration records existing grants without issuing goods or funds, and leaves the authoritative miner/hauler progress in its existing saved record.

`IHumanPresence` holds a lease for each selected flight session. Selection registers docked and undocked pilots; system changes transfer the lease; logoff, disposal and disconnect remove it. Chat authentication, channel membership and chat transport disconnections do not control the activity clock. An explicit session update entry point supports simulated sessions and future coordination. Presence is an internal trusted host contract, not a client request.

The human-active clock accumulates monotonic elapsed time once while any selection exists. Overlapping sessions do not multiply it. It retains fractions between timestamp samples and stores whole .NET ticks. Startup begins with no selected humans and resumes the saved elapsed value without offline catch-up. The resident owner captures the clock and relational image together; projection and file writing happen outside the owner. Failed saves retain the current state for retry. Production durability follows the ordinary checkpoint interval and graceful shutdown policy. The current maintenance loop uses this clock to admit legacy bot ticks, but still advances at most one legacy second per admitted call.

`CapsuleerJobQueue` orders current jobs by due time, pilot ID and action sequence. It keeps one ordered entry per agent, replaces externally revised jobs in logarithmic time, and rejects stale revisions. A turn admits at most 64 actions with a 10 ms soft budget; an atomic action finishes before yielding. The future host runner will use the defined 50 ms wake interval. Exceptions leave the failed action due. The turn result reports completed work, registered jobs, oldest due time and an overrun flag.

The job table stores the current action, due clock ticks, revision and action sequence. `CapsuleerPopulationStore.Settle` compares the expected identity and complete job version before invoking a feature mutation. The mutation and next job share one transaction. Duplicate callbacks do no work; rollback preserves both the old job and feature data. Callers use a fresh operation context after failure and update the resident queue only after commit. Current mining and hauling have not yet been connected to this transaction API, and migrated founders therefore have no scheduler jobs yet.

## Remaining global acceptance

- Provision each community in a resumable transaction. Use a saved world seed for repeatable names, origins and initial staggering over 30 active seconds. Preserve established ownership, routes and founder grants across configuration/catalog reconciliation.
- Replace dedicated role counters with shared deadlines while preserving paid mining, current navigation, trade commitments, health, tackle and deterministic random progression. Resolve background NPC acquisition, weapons, missiles, repairs and effect expiry through shared gameplay rules.
- Introduce world-owned Background, Activating, Active and Deactivating modes with generations. Settle preceding due work before human mutations and snapshots; yield between batches while those requests remain pending. Reject callbacks from older generations.
- Exercise entry/departure during alignment, warp, gate delay, lock completion, mining, docking, NPC fire, missile flight and effect expiry, including exact boundaries and multiple humans. Compare seeded active/background economic and combat outcomes.
- Benchmark approximately 1,804 real agents and larger populations, with no native scenes for background jobs. Measure due-action work, queue delay, memory and loaded scenes; retain warmed SQL/connection/fitting assertions and separate checkpoint capture, projection and writing.
- Expose population, system modes, due/overdue jobs, blocking reasons, failures, scene counts and timing through administration diagnostics.

The foundation's synthetic queue tests exercise 1,804 and 10,000 jobs. They do not establish full-population simulation cost, background combat equivalence or handoff correctness.

Gameplay references remain [EVE University mining](https://wiki.eveuniversity.org/Mining) and [hauling](https://wiki.eveuniversity.org/Hauling). Build-specific mechanics and historical Carbon/native observations remain in the [capsuleer protocol records](protocol/capsuleers.md).
