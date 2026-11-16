# Wormholes

## Resident connections â€” November 16, 2026

World version **171** replaces the three permanent reservoir links with resident connection slots. The first
generation preserves their source/destination choices when the pinned catalog contains them. Ordinary known-space
systems receive a replenished wandering connection. C1/C3 systems receive a known-space static; C2 receives a
known-space and a J-space static; C4 receives two distinct J-space static types; C5/C6 receive a J-space static.
Slot choices and density are Eden population policies, not Tranquility's per-system assignments. The ordinary-region
allowlist excludes Jove and other special known-space regions from both source and destination pools. Only classes 1â€“9
participate; Thera, small-ship shattered and Drifter connections are outside this population policy.

Type target classes, lifetime in minutes, total mass, daily mass regeneration and individual jump mass come from
build **3662168** dogma attributes **1381â€“1385**. Initial total mass varies deterministically within 90â€“110% of
the catalog limit. Destinations are selected from sorted, class-matching catalog systems and exclude the origin.
Source slots are populated on system observation. Incoming K162 signatures become visible after the first
successful transit, or deterministically after activation plus five minutes once no more than fifteen hours remain.
This passive reveal schedule is an Eden policy rather than a reproduction of the retail probability curve.

Warp requests activate the lifetime. Unactivated holes do not expire. A successful transfer deducts fitted ship mass,
records directional polarization and moves the ship in the same resident transaction. Rejection changes none of those
values. The final ship may cross with less remaining connection mass than its own mass; collapse then removes both
ends. Time expiry rejects at the exact saved deadline. Collapsed slots replenish after sixty seconds with a fresh
endpoint identity and position. Old item bookmarks, resolved signatures and pending transfers cannot acquire the new
connection. Coordinate bookmarks continue to identify their original location. The six legacy endpoint IDs are never
allocated to new connections. Regeneration caps at initial mass and cannot revive a collapsed connection.

`SavedWormholeConnection` stores slot identity, generation, endpoints, activation/expiry/replacement deadlines,
visibility and mass. `ResidentEconomy` owns these rows. Checkpoint projection is the sole durable write path;
there is no new offline SQL gameplay implementation. Saved polarization remains pilot/direction-specific. The
existing coherent resident owner serializes competing transits. Unchanged signature views retain their identity;
warmed observation and transfer tests assert zero SQL commands/connections and zero fitting recalculations.

### Pinned client and references

The original archive at `E:\EdenDev\3662168\.eden-original-code.ccp` was verified as SHA-256
`37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368`.
`WormholeSetup` in `evegraphics/controllers/clientControllerRegistry.pyj` accepts **1.0, 0.7, 0.4** size bands
and mass classes **1â€“4**, rather than kilograms. `infoContentBase.update_description_caption_and_sub_caption`
uses these fields for Show Info. The headless `wormhole_client_test.py` executes 48 combinations of the actual
native controller with inert model objects. No renderer, client session or game is started.

Gameplay references: [UniWiki wormholes](https://wiki.eveuniversity.org/Wormholes) and
[wormhole attributes](https://wiki.eveuniversity.org/Wormhole_attributes). The pinned catalog takes precedence
where modern reference values differ. Consulted local evejs build-3396210 `wormholeRuntime.js` (SHA-256
`3D20EB4D8041CB2E6C2FDB4F8EF7B3814067DE88951AFB40E9750531902D68C9`) and `wormholeAuthority.js`
(`D25683DF070E80FC8F2AFB2ADD87C39DE5541E7FD1F5DDF5FE0A8483504F9A7E`) for behavioral comparison only;
no reference implementation code was copied. Consulted Carbon Blue
[`Marshal.cpp` at fcbf0c2](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp);
the Blue codec and Destiny packet formats are unchanged. The existing wormhole jump effects and cancellation RPC
remain in use. There are no new live observations.

### Validation

**Automated validation:** Development began with exact filter
`FullyQualifiedName=Eden.Protocol.Tests.Exploration.WormholeTravelTests.WarmWormholeTravelKeepsPolarizationResidentUntilCheckpoint`.
The native controller check passed 48 cases. Lifecycle tests cover last-transit collapse, exact expiry, hidden exit
reveal, unchanged rejection, replenishment, stale identities and checkpoint/restart recovery. The combined affected
filter passed **241 cases** in **132.48 seconds** (feature build **2.69 seconds**). Solution restore/build passed
with zero warnings/errors. Both Python policy suites passed (8 catalog and 9 formatting cases). The final
resident-only scan-availability guard and a further post-restart ID-allocation assertion are covered by a subsequent
focused run. That assertion exposed a route-key/endpoint-sequence name collision after restart; the endpoint
allocator now has a separate checkpointed sequence, and the exact restart regression passed in 10.14 seconds.
The final wormhole/probing/initialization/migration filter passed **72 cases in 71.76 seconds** (feature build
**2.23 seconds**) after that correction. Formatting verification checked **2,341 files**; line-width,
documentation links and diff checks passed. This last filter was
`FullyQualifiedName~Eden.Protocol.Tests.Exploration.Wormhole|FullyQualifiedName~Eden.Protocol.Tests.Exploration.ProbeScanning|FullyQualifiedName~WorldInitializationTests|FullyQualifiedName~WorldMigrationTests`. No full .NET regression suite was requested or run.

Final affected filter:

```text
FullyQualifiedName~Eden.Protocol.Tests.Exploration.Wormhole|FullyQualifiedName~Eden.Protocol.Tests.Exploration.ProbeScanning|FullyQualifiedName~Eden.Protocol.Tests.Flight.FlightSessionTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.PilotGateTravel|FullyQualifiedName~Bookmark|FullyQualifiedName~WorldInitializationTests|FullyQualifiedName~WorldMigrationTests|FullyQualifiedName~Crimewatch|FullyQualifiedName~CombatAnomalyWorldTests|FullyQualifiedName~AnomalyWarpInTests|FullyQualifiedName~TypedFlightTests
```

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Inspect native stability/age labels while rolling a hole, and scan the replacement.
