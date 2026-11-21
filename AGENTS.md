# Eden contributor guide

## Project organization

- Put typed gameplay rules, state, commands and persistence interfaces in `Eden.Gameplay`. World owns catalog access, authoritative
  state and persistence. Protocol adapts client calls to Gameplay interfaces implemented by World. World and Protocol never
  reference each other. Server composes them and owns authentication, listeners and background scheduling.
- Keep catalog payloads and saved entities in `Eden.Data.Catalog` and `Eden.Data.World`. Keep contexts, mappings and migrations
  in World, shared registration and diagnostics in Infrastructure, and plugin contracts in Modding. See the
  [runtime guide](docs/architecture.md#projects-and-conventions) and [dependency checks](Directory.Build.targets).
- Group source files by feature and prefix namespaces with the owning project. Put tests in `tests/Eden.Protocol.Tests` and
  scripts in `tools`. Keep client files, resource caches and runtime data out of Git.
- Use the README for the project overview, quick start and build instructions. Track gameplay coverage and limits in
  [feature notes](docs/feature-notes.md). Record protocol findings, source revisions and validation on the owning topic page
  under `docs/protocol/`, link the record from the [protocol index](docs/protocol.md) and preserve evidence and anchors.
  [Documentation index](docs/README.md)

## Code style

- Follow [EditorConfig](.editorconfig) and the SDK policy in [global.json](global.json). Use `.\tools\Format.ps1` and
  `.\tools\Format.ps1 -Check`. The [formatting guide](README.md#formatting) covers the 140-character limit and exceptions.
- Use enums for related values and named constants for fixed IDs and limits. Document the source of build-specific values and
  client quirks. Keep numeric conversions at wire and persistence boundaries.

## Services and protocol calls

- Search the [EVE University Wiki](https://wiki.eveuniversity.org/) and check the EVE.js reference for gameplay or service work.
  Consult [Carbon](https://github.com/carbonengine) for engine behavior such as Blue, transport and Destiny. Verify build-specific
  behavior against Eden's pinned client and catalog.
- Register services with `[Injectable]` and constructor injection. Use transient handlers, scoped connection state and singleton
  shared infrastructure. Keep service resolution in Server and pass connection data to the operation that needs it.
- Give each service/method pair one owner per phase. Use `IPilotCallHandler` before selection and `ISelectedPilotCallHandler`
  after selection. Keep routing and selection gating in the router and output in the session loop. Preserve the
  `PilotCallResult` distinctions between unsupported calls, successful Python None, replies and errors.
- Use named immutable models for known requests, replies and events. Parse at dispatch with `BlueRead` for wire validation and
  serialize through `BlueModel.ToBlue()`, preserving native shapes and numeric types. Use `BlueRows` for shared row descriptors
  and keep dynamic Python values at explicit wire boundaries. Queue typed notifications and Destiny commands, preserving the
  before-flight queue. Keep codecs independent of DI. [Protocol model audit](docs/protocol/architecture.md#named-protocol-models-final-audit)

## Simulation performance

- Use the resident world for new gameplay and service operations, including persistence tests. Commit related changes atomically
  under the existing owner, publish after commit and persist durable state through checkpoints. At existing SQL boundaries,
  project dirty state before dependent transactions and preserve it on rollback.
- Capture a consistent generation under the owner and project and write checkpoints outside it. Preserve exact deadlines,
  retry safety and same-tick visibility.
- Share catalog inputs, fits and observer records with dependency-based invalidation. Use resident snapshots for warm observer
  and monitor reads, indexed lookups for hot paths and spatial candidates for contact checks. Keep per-shot work independent
  of total combat history.
- Compare identical warmed workloads and assert outcomes, SQL commands, connections and fitting rebuilds. Measure whole
  session frames and checkpoint costs separately, including populated systems and observers.
  [Runtime guide](docs/architecture.md#session-performance-diagnostics)

## Testing

- Build changed code and start with an exact method filter in `.\tools\Test-Feature.ps1 -Filter`. Cover affected behavior and immediate
  dependencies, reusing focused flows for small fixes. Follow the [README build order](README.md#build-and-run) for code milestones
  and the [testing guide](docs/testing.md) for coverage, failure handling and documentation checks.
- Run the full .NET suite only when the user explicitly requests a regression check. Live testing, including game launch,
  Computer Use and manual server acceptance, also requires an explicit request.
- Report automated checks, filters, results, elapsed time and limitations under **Automated validation**. Under **Live acceptance**,
  use “Not performed; agent client testing is disabled.” unless exercised. Under **Optional maintainer checks**, list up to
  three checks or state none.

## Milestones

Commit and push verified milestones. Confirm the commit on the remote branch before reporting completion or starting
the next milestone. Once a merge is verified on the remote target, remove the completed task worktree when clean.
