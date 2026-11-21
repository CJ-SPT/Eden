# Saved world configuration

[Documentation index](README.md)

The administration **World** page can [back up and wipe gameplay state](world-wipe.md) while retaining accounts and configuration.
Use **Optional data** to import or refresh [live market orders](#market-presets) and
[zKillboard traffic](capsuleer-population.md#zkill-backed-travelers).

Schema 99 adds immutable configuration revisions to each saved world. Upgrading captures its existing development supply, economic policies, adjusted prices, manufacturing indices and renewal schedule. Existing worlds retain their values; normal new-world defaults remain unchanged.

A draft saves as **pending restart**. Saving, restoring and importing use the same validation and optimistic generation check. Startup validates the pending revision before resource, market, combat and drone reconciliation. A rejected revision leaves the previous active revision in use and records its failure. Its history entry remains available; restoring a valid revision creates another pending entry. Revisions cannot be updated or deleted.

| Settings group | Editable server policies |
| --- | --- |
| Economy & market | Standard, Progressive, Testing, NPC-only and Custom market presets; relevant supply quantities/prices, LP-store datacores, reference history and service costs. |
| Gathering | Separate ore, ice and gas yield multipliers, mining hold capacity multipliers, and belt renewal settings. |
| Rewards | NPC bounty ISK, mission ISK, mission loyalty points and positive mission standing-gain multipliers. |
| NPC population | Enable new pirates/anomalies, guards, convoys and mining populations independently. Existing living residents remain. An optional Major Threat anomaly override enables the six advanced nullsec families without sovereignty upgrades. |
| Exploration | Always resolve cosmic signatures for every pilot, without scanner probes. Off by default. |
| Progression | Skill training speed, NPC kill-SP and mining-SP multipliers, and starting grants for new pilots. |
| Industry | Manufacturing, research, copying and invention speed; reprocessing efficiency. |
| Planetary industry | Maximum planets per pilot, extraction yield, factory output, command-center CPU and powergrid multipliers. |
| Structures | Minimum corporation age for anchoring; anchoring, core onlining and decommissioning speeds for new timers. |
| Simulated pilots | Enable or pause simulated pilots/miners, station traffic density (0–10×), and visitors per watched gate (0–16). |
| Gate camps | Enable camps, allow legal lowsec targets, tune shifts and departure chance, configure interdictor frequency and probe use. |
| PvP patrols | Enable nullsec roams, tune departure chance and the percentage of generated nullsec combat fits. |
| Pilot activities | Situational awareness, belt ratting and public combat anomaly ratting. |
| Simulation | Checkpoint interval, market maintenance interval and resource check interval. |
| Travel | Warp speed and align speed multipliers; autopilot gate landing distance, from 0 to 100,000 metres. |
| Fitting | Ship CPU, power grid, rig calibration and drone bandwidth multipliers. |

Changes affect future operations. Balances, installed jobs and paid costs, quoted obligations, historical transactions and saved deadlines are not rewritten. Existing grants are not replayed. Testing NPC market orders refresh under the selected policy on subsequent market operations; personal orders and escrow keep their saved terms. Disabling an NPC population prevents new spawns without deleting residents or changing their mechanics. Simulated-pilot limits also pause and despawn excess saved pilots at restart, as described below.

## World tuning

### Market presets

Open **Economy & market** to choose a preset, configure custom supply sources, or set service fees.
Existing settings are preserved; combinations that do not match a preset appear as **Custom**. Choosing a preset
changes market supply switches only. Wallet grants, balances, owned goods, player orders, service costs and
reference-history choices retain their settings.

The presets also apply to existing **Fresh-universe** worlds. The profile starts pilots at zero ISK with the
starter package. Its default market uses starter ore buyers and blueprint and skillbook offers. To select that
market again under **Custom**, enable progressive regional markets and turn off live orders, testing, regional
hub supply and beginner supply. Other presets use their own available buyers and sellers.

| Preset | Supply |
| --- | --- |
| Standard | Individual captured public live orders, with original supported stations, sides, prices, quantities, ranges, minimum volumes and expiration deadlines. |
| Progressive | Production unlocks plus regional NPC assortment and beginner supply. |
| Testing | Renewable development stock at the configured fixed price. Existing eligibility exclusions still apply. |
| NPC only | Corporation catalog offers at their normal stations. |
| Custom | Individual supply switches, including combinations from older settings. Progressive takes precedence over Testing. |

Standard requires `settings.economy.standardMarket=true`, `settings.economy.progressiveMarkets=false` and
`settings.developmentSupply.testingMarket=false`. It replaces generated NPC/progression supply while selected.
The existing hub and starter supply supplements do not alter imported orders. The stock and testing-price controls
are hidden for Standard because the imported quantities and prices are authoritative. Initial wallet grants
remain under Progression.

Before selecting Standard, open **Optional data** and choose **Import market orders**. Use **Re-import market
orders** to refresh the snapshot. The server downloads and validates the data in the background, and the page shows
the installed capture and the import result. Standard requires a compatible snapshot. Testing, Progressive and
NPC-only markets work without one.

Market imports require Python 3.8 or newer on the server. The Docker image includes it. On other installations,
use Python on the server's PATH or set `EDEN_PYTHON` to the executable. To import manually from the repository
root, point the importer at the server's installed catalog and write beside its saved world:

```powershell
python tools/Import-LiveMarket.py --catalog src/Eden.Server/data/catalog.sqlite --output src/Eden.Server/data/market-orders
```

Use the active catalog if skill/mod activation has selected a different catalog file. The importer downloads the
[EVE Ref full public order snapshot](https://docs.everef.net/datasets/market-orders.html), filters it against that
catalog and publishes a manifest plus immutable compressed regional files. It never modifies the saved world.
For reproducible offline imports, pass `--input path/to/orders.csv.bz2 --captured-at 2026-11-16T12:00:00Z`.
The capture timestamp must describe the source snapshot, not the import time.

The settings page reports capture time, compatible order count, regions and excluded orders. The manifest records
exclusion reasons: unsupported locations/items, location mismatches, already expired orders, or values outside
the pinned client's supported price/quantity/range limits. Player structures and newer catalog content are not
relocated or substituted. Public market data does not expose trader identities; local station corporations act
as counterparties. NPC-looking orders are finite too; Standard does not apply Eden's renewable NPC stock policy.

Choose **Standard**, save, then restart. Trading depletes resident orders and checkpoints preserve that depletion,
local order identities and transactions. Original live deadlines continue to run. Reopening the same captured
snapshot does not refill stock. A newer capture replaces imported quantities once per station on subsequent
market access and retires removed orders; it intentionally establishes a new external stock baseline. This is
an offline snapshot, with no network requests during gameplay and no automatic continuous synchronization.

Keep `market-orders/` beside the world when deploying or restoring it, including its manifest and referenced
generation directory. If Standard is already active and the files are missing or incompatible, restore or re-import
the snapshot with the command above before starting Eden. The files are runtime data excluded from Git. Failed imports
leave the previous manifest active. Servers keep their current immutable snapshot until restart, so an import can be staged while running.
Do not remove a generation still referenced by a running server. Selection and activation reject missing or
incompatible manifests; regional checksums and order validation reject corrupt data before publishing orders.

Native protocol and automated evidence: [Standard market implementation](protocol/industry.md#standard-live-order-market-and-unified-economy-settings-november-16-2026).

The Settings page groups controls by pilot activity, industry and trade, and world simulation. **Economy & market**,
**NPCs & exploration**, and **Player bots** each have their own section. Open a **?** beside a setting for units,
limits, dependencies or examples. Category changes retain the shared draft.

**Structures → Minimum corporation age for anchoring (days)** sets `settings.structures.minimumCorporationAgeDays`.
Set it to **0** to remove the age wait, or keep the default **7** days. Values must be nonnegative whole days;
historical documents that omit the field keep seven days. Use **Save for restart** and restart the server to activate it.
This controls new Upwell deployment attempts. A player corporation and structure-management authority are still required.
Anchoring, onlining and decommissioning settings continue to preserve existing saved deadlines.
Switching categories retains the draft. Activity reward/yield multipliers show their unit, range, explanation and an illustrative example.
**Reset to 1×** resets that control; **Reset draft to original** restores the world's original revision into the draft.
**Discard unsaved changes** returns to the last loaded or saved draft, including any already pending revision.
The shared **Save for restart** bar shows unsaved, active or pending-restart status. Review the changed-values summary before saving;
expand **Original, active and pending values** for the complete comparison. Economic overrides are under Advanced.

**Travel → Autopilot gate landing distance (metres)** controls the requested stand-off for future autopilot
gate warps after restart. Set it to **0** for close gate landings, or keep **15000** for the usual approach leg.
The existing hull clearance and deterministic warp landing offset still apply. Manual warp ranges and autopilot station
landings retain their existing/requested distances. Historical configuration documents that omit Navigation use 15000 metres.

All new multipliers default to **1× current Eden behavior**, including when loading historical documents that omit the new fields.
Gathering, travel, planetary industry, structure/industry speed and reprocessing multipliers accept **0.1–100×**. Reward multipliers accept **0–100×**;
zero disables that reward. Forms, command imports and the administrator API enforce the same bounds. These policies belong to the
saved world; they do not modify shared catalog definitions or autonomous NPC gathering/production.

- **Gathering → Mining hold capacity** sets separate **0.1–100×** multipliers for general mining, dedicated ice and dedicated
  gas holds. With **Save for restart**, the next server start scales existing hold capacities after ship and skill bonuses.
  A Venture’s 5,000 m³ general mining hold becomes 10,000 m³ at 2×, including when it carries ice or gas. The active ship’s
  dogma attributes and inventory use the tuned capacity. Ordinary cargo and static type-only Show Info retain their own
  values. Existing contents stay in place when a hold shrinks; unload cargo to make space for new material.
- Ore and ice controls also apply to player mining drones after drone skill, hull and rig bonuses. Delivery respects tuned
  mining holds, and extraction shares configured mining SP rewards. Settings still activate on restart. See
  [utility and mining drones](protocol/drones.md#utility-and-mining-drones-november-15-2026).
- Ore, ice and gas controls scale extraction volume after ordinary equipment and skill bonuses. Cargo space and remaining deposits
  still limit extraction, and existing critical/residue rules remain. Ice requires a completed cycle; its control changes blocks
  per completed cycle rather than cycle duration. Ore and gas retain proportional partial-cycle collection.
- Bounty ISK scales the gross amount before corporation tax. Selecting an NPC in space shows that gross amount.
  Kill SP uses that multiplied gross bounty before corporation tax or fleet sharing. The configurable base rate defaults to
  0.25% of gross bounty ISK, multiplied by the kill-SP setting, then rounded down to whole points. For example, a 3,000 ISK NPC
  with 10× bounty and 20× kill SP awards 1,500 SP. Bounty ISK set to zero also disables kill SP. These credits remain silent.
- Mining SP uses the reference ISK value of units actually delivered to the hold: multiply that value by the mining-SP
  setting, divide by the configurable reference ISK per SP (default 400) and round down once per collection. Yield multipliers also scale SP
  when they increase delivered resources. One ice unit is a whole block: a single block can award SP based on its value,
  and 3 blocks award three blocks' value before rounding. Critical cargo counts; residue, full holds and empty interrupted
  ice cycles do not. Credits are silent and saved atomically with the harvest. The Progression control accepts 0–100×,
  defaults to 1× for existing worlds, and activates after **Save for restart** and a restart. Zero disables mining SP.
  The Progression panel exposes both base conversions: `progression.killSkillPointPercent` accepts 0–100%, and
  `progression.miningIskPerSkillPoint` accepts 0.01–1,000,000,000 ISK per SP. Lower mining costs grant more SP.
  Omitted base fields retain 0.25% and 400 in existing revision documents. These rates are saved, exported and compared
  with original, active and pending values, and activate after restart.
  Live draft previews compare against active settings using editable example amounts, with ore/ice/gas selection,
  combined multipliers, effective conversion and SP per kill or collection. Example amounts are not saved.
- **Progression → Skill training speed** (`settings.progression.trainingSpeedMultiplier`) accepts **0.1–100×** and defaults
  to **1×**, including for older revisions. Select **Save for restart** and restart the server to activate the rate. At 2×, a
  newly scheduled level with a normal one-hour duration takes 30 minutes. Character attributes, implants, cerebral accelerators
  and clone grade still determine the underlying rate. Saved queue deadlines and accrued SP carry across restart. Pause and
  resume an existing queue to reschedule all remaining levels at the active rate. The client receives the rate at login and
  uses saved deadlines for scheduled training. See [skill training speed](protocol/characters.md#world-skill-training-speed-november-21-2026).
- Mission ISK, LP and positive standing gains are calculated when an offer is created and saved with it. Existing offers and
  accepted missions keep their terms. Standing gain fractions cap at 100%; penalties remain unchanged.
- Industry speed divides normal duration: 2× means half the time, retaining ordinary skill/blueprint bonuses and rounding.
  Research covers both material and time efficiency. Invention speed does not change success chance or output runs.
  Installed jobs retain their saved deadlines. The minimum duration is one second; very short native previews can round to zero,
  which installation normalizes to that minimum before checking the authoritative duration.
- Reprocessing scales effective efficiency after skills and caps recovery at 100% of available materials, before output rounding.
  The quote and committed outputs use the same calculation.

Combat difficulty, travel, ship-loss rules, loot and salvage tuning are outside this version. No client package
update is required. Native visual presentation remains unverified; see the [implementation record](protocol/world-configuration.md).

## Custom gameplay options

All switches below default to **on**, including older configuration documents that omit them.
Use **Save for restart** to stage edits and restart the server to activate them.

| Admin control | Exported setting | Effect when disabled |
| --- | --- | --- |
| Economy & market → Empire LP-store datacores | `settings.economy.empireLpDatacores` | Ordinary empire stores offer their original assortment. Militia stores retain their own datacores. |
| Economy & market → Simulated market history | `settings.economy.simulatedMarketHistory` | History contains actual local trades only. Sparse history can leave native tickers and charts empty. |
| Economy & market → Regional hub supply | `settings.economy.regionalHubSupply` | Hubs use their corporation's institutional assortment. Added global NPC sell offers retire on market preparation. |
| Economy & market → Beginner market supply | `settings.economy.beginnerMarketSupply` | Starting stations and hubs require ordinary production unlocks for generated product sell offers. Original corporation offers still apply. |
| Progression → Grant civilian starting equipment to new pilots | `settings.progression.grantStartingEquipment` | Newly created pilots receive their starting hull and configured skills/wallet grants without the additional civilian miner and weapon. |
| Player bots → Enable player bots | `settings.capsuleers.enabled` | All simulated capsuleers pause and disappear from space, Local, station guests and scans after restart. |

The player-bot control uses the existing capsuleer master switch and covers travelers, miners, haulers and ratters.
Re-enabling resumes eligible saved bots with their possessions and progress. Miner, traffic-density and gate-visitor
controls continue to limit the enabled population. See [autonomous population](#autonomous-population).

Beginner supply operates within progressive regional markets. Regional hub supply controls added institutional
NPC sell assortments independently, including skillbooks, commodities, command centers, quantum cores and blueprints.
Missing snapshot sell quotes use positive catalog base prices. Already purchased items, existing pilots' equipment, actual trade
history, wallets and personal orders retain their saved state. The controls participate in revision comparisons,
history and export/import. See the [implementation record](protocol/world-configuration.md#custom-gameplay-switches-november-15-2026).

## Exploration

Open **Settings → NPCs & exploration → Probe scanning** and enable **Always resolve cosmic signatures**,
then choose **Save for restart**. After the next server start, current-system signatures appear at 100%
with their exact positions and available site details for every pilot. Warping and bookmarking require no probes.
This includes combat/DED, data, relic, gas and wormhole signatures supported by the world.
Newly spawned signatures are revealed automatically; retired sites lose access as usual.

The option defaults to off, including when importing older configuration documents. Exported configuration uses
`settings.exploration.alwaysResolvedSignatures`. Turning it off and restarting restores normal probe scanning;
automatic visibility does not permanently add per-pilot scan resolutions. Previously earned probe resolutions
and saved bookmarks retain their normal behavior. Site populations, rewards and exploration equipment stay unchanged.

## Travel

Open **Settings → Travel**, edit the draft and choose **Save for restart**. The next server start applies the policy
to resident player-ship fits after normal hull, skill and module bonuses. Defaults preserve existing behavior.

| Option | Default | Effect |
| --- | --- | --- |
| Warp speed | 1× | Multiplies fitted AU/s. Native warp acceleration and braking derive from that speed, retaining their existing limits. |
| Align speed | 1× | Divides fitted agility (inertia modifier). 2× halves inertia time, including turning and subwarp acceleration. |
| Autopilot gate landing distance | 15,000 m | Requested gate stand-off; 0 lands close to gates with existing hull clearance and landing offsets. |

Try **2× warp speed, 1.25× align speed and 0 m autopilot gate landing** for a moderate solo convenience setup.
Align speed leaves mass and maximum subwarp velocity unchanged. Exact time savings depend on the hull, fit, starting
velocity, warp distance and simulation ticks. Warp speed alone does not shorten alignment. Gate transitions, arrival
timers, warp disruption, minimum warp distances and gate activation ranges retain their existing rules.
NPC travel profiles and static catalog type attributes are unchanged. Active-ship dogma and native brain modifiers
carry the same travel multipliers used by the resident fit; cached reads and reconnects do not compound bonuses.

Exports retain the existing `settings.navigation` group for compatibility:

```json
"navigation": {
  "autopilotGateLandingMeters": 0,
  "warpSpeedMultiplier": 2,
  "alignSpeedMultiplier": 1.25
}
```

Older exports missing the multipliers load them as 1×; exports missing the entire navigation group also keep the
15,000 m gate default. Forms, imports and command/API validation share the 0.1–100× multiplier bounds.
See the [protocol record](protocol/navigation.md#travel-configuration-november-15-2026).

## Fitting

Open **Settings → Fitting**, edit the World settings draft and choose **Save for restart**. Each
multiplier accepts **0.1–100×** and defaults to **1×**, including in older configuration exports. The
server applies the factors to fitted ship budgets after hull, skill and equipment bonuses.

| Option | Effect |
| --- | --- |
| Ship CPU | Increases or decreases CPU available to online modules. |
| Ship power grid | Increases or decreases power grid available to online modules. |
| Rig calibration | Changes the total calibration available when installing rigs. |
| Drone bandwidth | Changes the bandwidth available for drones in space. A hull with zero bandwidth still has zero bandwidth. |

At **1.5× CPU and 2× power grid**, a skilled and fitted ship with 200 tf and 50 MW gets 300 tf and
100 MW. Module costs, required skills, slots, hardpoints, drone bay capacity and drone control count
follow their normal rules. Lowering budgets leaves existing gear installed. Normal fitting
reconciliation can offline modules that exceed the new CPU or power grid budget. Newly installed rigs
and online operations must fit within the applicable budget.

Exports use `settings.shipFitting` with `cpuMultiplier`, `powerGridMultiplier`,
`calibrationMultiplier` and `droneBandwidthMultiplier`.
See the [protocol record](protocol/fitting.md#configurable-ship-fitting-budgets).

## Planetary industry

Open **Settings → Planetary industry** to set **Maximum planets (0 = skill-based)** and six independent
**0.1–100×** multipliers, all defaulting to **1×**. The planet limit defaults to **0**, preserving one planet plus
the pilot's Interplanetary Consolidation level (one to six planets). Set **1–100** to replace the skill-based count
with that total per pilot. For example, **12** allows each pilot up to twelve colonies regardless of that skill's level.
Save for restart, restart the server and reconnect to apply the change.
The colony directory's total slots follow the configured count, and command-center construction uses that
same limit. **100 means 100 total colonies per pilot.** Each colony still requires deploying its matching command center.

Lowering the limit or restoring **0** preserves all existing colonies and their production, editing and abandonment.
A pilot at or above the new limit cannot deploy another colony until enough colonies are abandoned.
Omega status, planet eligibility, command-center cargo, construction costs and building/upgrade skills still apply.
With an explicit limit, Interplanetary Consolidation no longer restricts skill extraction; Command Center Upgrades
remains protected when existing command centers require it.

| Control | Effect after Save for restart and restarting the server |
| --- | --- |
| Extraction yield | Scales the base yield of newly installed extractor programs and their survey quotes. Existing programs keep their saved yield and expiry. |
| Factory output | Scales the product units per completed schematic cycle. Ingredients and input buffers stay unchanged; Factory speed controls duration separately. Applies to outstanding cycles and offline catch-up processed after restart. |
| Factory speed | Divides processor cycle duration by this value, with a one-second minimum. 10× turns a 30-minute basic cycle into 3 minutes and a 60-minute advanced/high-tech cycle into 6 minutes. Extractor timing stays unchanged. |
| Planetary storage capacity | Scales launchpad, storage-facility, command-center and per-pilot customs-office capacity. 100× gives each launchpad 1,000,000 m³ and each customs office 3,500,000 m³. Processor input buffers retain one batch. |
| Customs tax (%) | 0–100%, default 17%. Customs exports pay this percentage of taxable commodity value; imports pay half. 0 disables both customs fees. Command-center launches retain their separate 15% fee. Applies after restart. |
| Command center CPU | Scales available CPU at all six command-center levels. Buildings, extractor heads and links still consume their normal CPU. |
| Command center powergrid | Scales available powergrid at all six levels. Buildings, heads and links still consume their normal power. |

Production rounds down to whole units with a minimum of one for a positive batch, including P4 recipes.
Routes retain their saved quantities. **Resize output routes and upgrade links** to collect
increased production; unrouted output and overflow are still lost. Faster factory cycles increase bandwidth required
by both processor input and output routes. Existing links and routes are not automatically changed. Storage-only
increases need no link changes. When lowering output,
remove or recreate routes that exceed both the original and newly configured factory output before submitting edits.
Lowering CPU or powergrid preserves existing buildings; remove enough buildings/links or upgrade the command center
so the complete colony fits before submitting an edit.

Factory speed also recalculates a running processor's deadline from its saved cycle start. Work already due under
the new duration completes once at the colony's last committed simulation time; prior events are never replayed.
Offline catch-up then uses the active speed. Lowering speed can extend a running cycle. Input quantities per batch,
extraction program deadlines and transfer cooldowns stay unchanged. Faster factories still need enough materials:
an extractor route feeding only one ordinary basic-factory batch per half-hour will need a larger quantity to sustain
faster P1 production.

Lowering storage capacity preserves existing goods, including overfilled buildings and customs offices. Export or transfer them out
before adding more. New imports and production deliveries use the reduced limit.

The signed login adapter synchronizes native planet-limit validation, storage capacities, CPU/power previews,
factory products and cycle times, including the customs-window launchpad capacity and volume-per-hour displays. Reconnect after server restart;
no separately rebuilt client package is required. Native rendering and interactive gameplay remain unverified.
Extraction quotes and saved program parameters already carry the configured yield through the ordinary protocol.
Policies and colony progress use the existing world configuration and resident checkpoint paths.

Configuration exports store these values under `settings.planetaryIndustry`:

```json
"planetaryIndustry": {
  "extractionYieldMultiplier": 1,
  "factoryOutputMultiplier": 1,
  "cpuMultiplier": 1,
  "powergridMultiplier": 1,
  "maximumPlanets": 0,
  "storageCapacityMultiplier": 1,
  "factorySpeedMultiplier": 1
}
```

Historical configuration documents that omit this group retain 1× behavior; an omitted `maximumPlanets` retains
the skill-based limit. Omitted `storageCapacityMultiplier` and `factorySpeedMultiplier` each retain 1× behavior.
Drafts, original/active/pending comparisons, imports, validation and restore use the same settings.
See the [multiplier implementation record](protocol/planetary.md#world-configuration--november-14-2026) and
[configurable planet limit](protocol/planetary.md#configurable-planet-limit--november-16-2026), plus the
[capacity and speed implementation record](protocol/planetary.md#storage-capacity-and-factory-speed--november-16-2026).

## Progressive regional markets

The Economy card exposes **Progressive regional markets**. Save the setting and restart the host to activate it. The saved `economy.progressiveMarkets` value accepts true or false. An omitted or null value follows the world's supply mode, enabling progression for economy saves and retaining configured development prices for development saves. This switch can be staged on an existing save and preserves personal orders, escrow, balances, inventories and history. Progression now means [claimed-production unlocks](protocol/industry.md#production-driven-market-progression--november-8-2026), rather than timed product unlocks. New server worlds start with testing market supply disabled; `--testing-market` explicitly creates a development market. Existing saves retain their saved supply mode.

Markets use one shared selected-human clock. Economic age and replenishment advance while at least one human pilot is selected, including while docked. Additional humans share the same elapsed interval. Training and installed industry retain their established deadline policies.

NPC suppliers follow the pinned corporation offers. Generated sell stock requires a delivered manufacturing claim.
Resources and non-faction Tech I items have NPC buyers immediately; supported advanced products require a production
claim for demand. Hubs start with approximately 150 million ISK of demand per type, growing through eight-hour stages
to 600 million after 24 active hours. Specialist stations have one fifth of the budget. Finite quantities recover at
ten-minute boundaries, retain partial intervals and move quotes by at most fifteen percent as they deplete. Blueprint,
skillbook, faction and supercapital exclusions are described in the [industrial demand record](protocol/industry.md#industrial-npc-demand--november-9-2026).

Regional quotes are prepared lazily for the current station, its regional hub and previously prepared stations in that
region. Direct trades and NPC purchases from competitively priced personal sell listings consume the same finite demand.
Quotes use the embedded real-market snapshot, independent of adjusted prices used for service fees. Station roles use
catalog market/manufacturing facilities, gate connectivity and station counts. The superseded time-unlock/material-price
policy remains in the [historical protocol record](protocol/industry.md#progressive-regional-markets--november-8-2026).

## Overrides and compatibility

Supported overrides change the server-supplied **adjusted price** for an existing catalog item or the **manufacturing index** for an existing system. The client obtains these values through `config.GetAverageMarketPrices` and the industry facility response. IDs must exist in the installed catalog, prices must be positive, indices must be within zero and one, and entries cannot repeat. Enabling service costs requires adjusted prices for catalog materials with positive source base prices.

The same native price feed exposes a separate `averagePrice` for inventory and other estimated values, even with service
costs disabled. It uses the existing snapshot midpoint or sole observed quote side. Simulated reference history
supports the native ticker and charts. Adjusted-price overrides
change industrial fee inputs and do not change inventory estimates or NPC trade prices.

These overrides are economic projections belonging to the saved world. They do not modify the original item payload, base price, recipe or shared catalog cache. Two worlds using the same catalog retain separate active and pending definitions. Skills use the separate [skill editor and signed client package workflow](#skill-editor). Other native-client physics, volumes, recipes, identities and schema fields remain read-only. Adding another writable field requires an explicit compatible server/client synchronization contract.

Each document carries format version 1 and the installed catalog source fingerprint. Unknown properties, incompatible catalogs and invalid settings are rejected. Decimal economic inputs retain their precision in saved documents and existing SQLite projections. Original catalog files are never written.

## Local graphical editor

Provision a separate administrator from a local terminal. This command prompts for a password and confirmation; it does not change game accounts or activate pending configuration:

```powershell
dotnet run --project src/Eden.Server -- --world 'C:\Eden\world.sqlite' --set-admin-password Corey
```

Start the server normally, then open **http://127.0.0.1:26003**. The listener starts only after an administrator has been provisioned. Use `--admin-port <port>` for a different dedicated port or `--disable-admin` to disable it. By default it binds IPv4 loopback and requires a local peer and Host. Setting `EDEN_ADMIN_ORIGIN` opts into HTTPS administration using the configured server listen address and certificate, with requests restricted to the configured browser origin. Game accounts receive no administration access. [Docker setup for remote administration](docker.md#remote-administration)

**Changed values** compares the draft with the last saved configuration, including edits made in other categories.
Expand **Original, active and pending values** to review revision values. **Reset draft to original** copies the
original configuration into the draft. **Save for restart** validates and durably stages the draft. The server
activates the saved revision at restart. Revisions provides comparison, restore, JSON export and validated file import.
Restoring also creates a pending revision.

Use **Player bots** to configure individual bot activities. **Population** shows the active values after restart.
[Autonomous population settings](#autonomous-population)

Catalog provides searchable pages across all 442 generated datasets, typed fields, explicit missing/null distinctions, exact source numbers and relationship navigation. Nested collections have their own display pages. Native fields remain read-only in original, active and pending views. Generated types validate records before the browser displays their original payloads; dynamic JSON stays inside this administration/storage boundary.

The editor uses a dark theme throughout settings, catalog browsing, revision history and sign-in, including native form controls, validation messages and keyboard focus indicators.

## Skill editor

**Current workflow — November 9, 2026:** Skill authoring has moved to the offline Avalonia
[Eden Modding SDK](modding.md). Use local administration's **Mods** page to install/select/build packages;
the web skill-authoring page and its API endpoints have been removed. Export existing drafts/history with
`--export-skill-project ABSOLUTE_FOLDER` and open that folder in the SDK. The records below preserve the
historical web-editor implementation and its evidence; its old navigation instructions are historical.


Open **Skills** in local administration. Search original and custom skills by name, description or ID; several words can appear in any order. The form edits names, descriptions, existing skill groups, skillbook base prices, publication, icons, market groups, training multipliers, primary/secondary attributes, up to six prerequisites and each race's Alpha limit. Zero Alpha levels require Omega; the normal five-level progression remains unchanged. New skills receive server-assigned IDs from a reserved range, checked against installed types and retained custom identities.

The editor has three sections: **Identity & training**, **Bonuses**, and **Requirements & access**. Switching sections retains draft values. The save/discard controls and draft/publication status stay visible while scrolling the form. The status distinguishes unsaved edits, a saved draft ready to publish, a published package awaiting restart, and changes active on the server. **Skillbook appearance and market** holds the optional icon and market-group fields. **Publish saved changes to game clients** expands the packaging controls above the editor.

Primary and secondary learning attributes select which pilot stats determine training speed: skill points per minute are primary plus half the secondary value, before account modifiers. Training multiplier (rank) changes the skill points required, not bonus strength. Bonus attributes are different: they store numbers used by effects. For example, a bonus attribute can store `25`, while an effect applies that value as a percentage to selected equipment. Adding an attribute alone does not create a gameplay bonus.

**Bonuses** opens directly on **Build a bonus**. Search for a stat such as velocity, damage, cargo or mining, select **What changes**, then select **Applies to**. Targets come from compatible existing rules; a stat with one target selects it automatically. Choose **Increase** or **Decrease**, enter a positive **Amount**, and choose **Per skill level** or **Once trained**. The summary explains the requested result before **Add bonus** changes the draft. Search and composer selections do not mark the saved definition dirty; adding or editing a bonus does.

The builder supports single-stat percentage changes, flat additions and fixed multipliers. It connects a compatible level-scaling rule automatically where one exists; fixed or inline scaling is reflected in the Apply control. Multipliers are edited as percentage changes rather than raw factors: a 25% increase stores 1.25 for a multiplier rule. Existing recognized bonuses use the same Increase/Decrease, Amount and Apply controls, with their targets and level I–V previews visible. Removing a card removes its application and separate scaling rule together, retaining the stored value for reuse.

**Technical rules** retains the complete original rule/attribute editor. Unknown effects, multiple-output rules, intermediate bonus attributes, duplicate values/rules and inputs shared by unrelated bonuses remain there. They are preserved without silently changing their meaning. The builder rejects overlapping bonuses and changes that would alter another rule's shared strength. Original skills with dedicated calculations remain read-only in both views.

Skill groups, prerequisites, technical attributes/effects, icons and market groups retain searchable named choices. Expand **Find …** when needed; each technical list shows at most 30 matches and keeps its current selection visible. The bonus builder instead searches and groups choices by the resulting stat and target, and does not require effect IDs or source attributes. Prerequisite and technical rows align their inputs and removal buttons on wide screens, and stack vertically on narrow screens. The builder's direction, amount and scaling fields also align and stack together.

### Example: 25% ore mining yield per level

1. Choose **New skill**, or **Create a copy** of a suitable skill, and enter its name and description.
2. Open **Bonuses**, search for `mining`, and select **Ore mining yield (%)** in **What changes**. Choose fitted items requiring Mining in **Applies to**.
3. Choose **Increase**, enter **Amount** `25`, select **Per skill level**, then **Add bonus**. Its card shows +25%, +50%, +75%, +100% and +125% extra yield for levels I–V. Levels add: level V grants +125%, not 25% compounded five times. Other bonuses can multiply the result.
4. Set prerequisites and Alpha limits in **Requirements & access**, then save. Build/stage a client package and restart the server to activate the saved definition as described below.

The recipe reuses pinned build-3662168 Mining/Astrogeology rules: effect `389` multiplies attribute `434` (miningAmountBonus) by trained skill level, and effect `391` applies the resulting percentage to mining amount on fitted items requiring Mining. The per-level option is offered only when the installed catalog has a compatible scaling rule. It covers ore mining lasers and strip miners requiring Mining; it does not boost ice harvesters, gas harvesters or mining drones. [EVE University's mining guide](https://wiki.eveuniversity.org/Mining) describes the ordinary Mining/Astrogeology yield bonuses; the editable recipe uses the pinned catalog and Eden's existing modifier graph. The original Mining/Astrogeology skills retain their dedicated gameplay owner and remain read-only for bonus editing.

For a recognized existing flat bonus, change **Apply** to **Per skill level** to retain its percentage and add the compatible scaling rule. For legacy mining rules such as online effect `1882`, **Make it scale with skill level** retains its percentage, replaces the equivalent application rule, and installs the passive `389`/`391` pair. It does not stack a second mining application on top of the old one. A stored `20` becomes +20% at level I, +60% at level III and +100% at level V. The form warns when a mining application has no level-scaling rule. Writing “per skill level” in the description cannot change this math. Save and publish the repaired definition, restart the server, and let the launcher install the matching package. Existing published definitions are not silently rewritten by this update.

New skill bonuses require supported category-0 passive effects. Online (category 4), active and targeted module rules are excluded from choices and rejected by authoring writes. Existing unsupported values remain visible for review; replace them before saving a custom definition. In Advanced, **Add effect rule** combines supported existing rules and their input values. Per-level scaling and target application can require separate effects. Targets and operations belong to the selected catalog rule; arbitrary targets, new rule implementations, new skill groups/icons and gameplay scripting cannot be authored here. Original skills with dedicated calculations remain read-only, with forged writes rejected by the server.

Search ignores case and punctuation and combines words across names, descriptions and gameplay terms. Exact IDs and readable names rank before description-only matches. For example, `mining yield` finds both ore-yield application and skill-level scaling rules despite their internal miningAmount names. Matching choices appear as clickable results below the search box, with at most 30 per picker. The sidebar retains every match across pages. Search indexes share the immutable choice collections and do no catalog reads.

Searching/paging the skill list updates the browser separately from the draft form. Catalog definitions, lookup labels and typed effect metadata are loaded in batches and shared across editor visits; searching does not read the catalog. Saving and reloading preserve the list's search and page (clamped if its results shrink). The editor rechecks saved generations and administrator access for every operation.

**New skill** creates a definition; **Create a copy** starts from the selected skill. **Save skill** durably saves that draft without changing the running game. Resetting a draft discards unsaved edits. **Restore original values** saves a fresh revision containing the original catalog definition. Revision history can restore earlier saved versions, including removed custom skills. Restored originals leave the edited-only list. Stale saves are rejected while retaining the draft.

Only user-created skills have a removal button. The same restriction is enforced by the server and immutable stored provenance. A skill required by another current definition cannot be removed until that prerequisite is changed. Removal retains its ID, history, existing skill points and personal assets; the published retired definition becomes unpublished, loses its passive effects and is no longer supplied by NPCs or injectable. Existing character records can therefore still display a retired skill. IDs are never reused.

### General skill bonus builder — November 9, 2026

The mining-only preset flow has been replaced by the stat/target/strength builder described above. Typed choice metadata now carries the target stat, target scope and unit directly from the pinned catalog. The UI discovers compatible single-output passive rules and their scaling partners, instead of requiring a user to choose effect pairs. It recognizes safe existing pairs, keeps direction/strength edits attached to the same stored input, and preserves technical combinations that cannot be edited independently. Supported targets and rule implementations remain catalog-defined; this does not add arbitrary gameplay scripting.

The ordinary navigation and mining references are [EVE University's navigation skills](https://wiki.eveuniversity.org/Skills:Navigation) and [mining guide](https://wiki.eveuniversity.org/Mining). Their percentage-per-level descriptions are gameplay references, checked November 9; actual authored values and targets use build 3662168 and the existing authoritative modifier graph. No Blue, transport or Destiny behavior changed.

**Automated validation:** After integrating main `563f0766`, tool restore, solution restore/build (zero warnings/errors; 6.05 seconds), all 53 focused cases (8.74 seconds of runner time, no skips), both Python policy suites (8 catalog-setup cases in 0.730 seconds; 9 formatting-policy cases in 0.025 seconds) and formatting verification passed. The affected filter combines `SkillBonusBuilderTests`, `SkillBonusEditorTests`, `SkillBonusPresentationTests`, `SkillEditorUsabilityTests` and `SkillEditorTests` with `FullyQualifiedName~Eden.Protocol.Tests.Administration.<class>` alternatives. The package integration case uses an isolated copy of the pinned original client archive/manifest/runtime, leaving installed clients unchanged. Development began with exact filter `FullyQualifiedName=Eden.Protocol.Tests.Administration.SkillBonusBuilderTests.EmptyEditorShowsStatTargetStrengthAndBasisBeforeAnyClickAndComposesSelectedSpeed` through `tools/Test-Feature.ps1` (one pass before direction variants were added; build 9.71 seconds, runner 3.12 seconds). Coverage includes the empty authoring form before any click, a non-mining choice, increase/decrease and scaling modes, inline scaling, independent bonus removal, rejected overlaps/shared inputs and unchanged-state checks, saved/reopened custom definitions, actual ship speed at levels 0/I/III/V, and existing mining module/protocol behavior. No full .NET suite was requested.

The headless layout script passed 150 row/control checks across five viewport widths (1480, 1024, 900, 768 and 375 pixels), collapsed/expanded searches, populated and technical rows, the empty composer, a composed decrease, and a ready-to-add speed bonus. It checks native displayed direction/scaling values and checks that the empty screen already exposes the stat, target, direction, amount and Apply fields. Development checks caught and corrected a selector showing Increase for a stored decrease, an initially mismatched default Apply label, a duplicate synthetic fixture ID, and an assertion depending on which of two valid rejection messages appeared first. No full .NET regression suite or manual server/gameplay session was run.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Restart the server and inspect the empty Bonuses editor; create a custom bonus for a non-mining stat and review its summary and level preview.

### Guided bonus cards and row alignment — November 9, 2026

The mining editor now treats its level-scaling and application effects as one bonus. Adding, repairing and removing a guided bonus updates the linked rules together; editing its percentage uses the existing shared attribute. Opening either view does not normalize a saved definition. Unrecognized, duplicated or unrelated shared-input combinations require technical editing. No gameplay formulas, protocol fields or client archive adapters changed. The gameplay reference remains [EVE University's mining guide](https://wiki.eveuniversity.org/Mining), checked November 9; exact targets and effects remain pinned to build 3662168 as documented above.

**Automated validation:** Tool restore, solution restore/build (zero warnings/errors; final build 0.88 seconds), both Python policy suites (8 catalog-setup tests in 0.731 seconds and 9 formatting-policy tests in 0.024 seconds), and formatting verification passed. Development started with exact filter `FullyQualifiedName=Eden.Protocol.Tests.Administration.SkillBonusPresentationTests.LayoutFixtureRendersOneBonusInputAndThreeDistinctPrerequisiteRows` through `tools/Test-Feature.ps1` (one pass; build 7.29 seconds, test runner 7.11 seconds). The final affected filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Administration.SkillBonusEditorTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.SkillBonusPresentationTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.SkillEditorUsabilityTests|FullyQualifiedName~Eden.Protocol.Tests.Administration.SkillEditorTests
```

All 40 affected cases ultimately passed. The final group produced 39 passes and one source-fixture failure in 9.39 seconds: the selected installation had a changed archive and no original backup. Retrying only the package case against the prepared installation reached a modified-manifest rejection. A separate test source copied the verified original archive, original manifest and runtime DLLs; the exact `PublishedPackageActivatesOnRestartAndCustomBooksTrainPersistAndRetireSafely` filter then passed in 6.93 seconds. Installed clients were read only. This integration covers archive generation, authenticated download/tickets, restart activation and persistent training/retirement. No full .NET regression suite was requested or run.

The automated browser check passed 90 alignment checks across guided and technical views at widths 1480, 1024, 900, 768 and 375 pixels, with searches collapsed and expanded. It verifies input/button bounds, stacked narrow rows, a single guided percentage input and no page/control overflow. These checks use HTML rendered from production components and the actual administration stylesheet; they do not establish live browser navigation or interactive client acceptance. Reproduce with Python Playwright and its Chromium installed:

```powershell
$env:EDEN_SKILL_LAYOUT_DIRECTORY = "$PWD/artifacts/skill-editor-layout"
.\tools\Test-Feature.ps1 -Filter 'FullyQualifiedName=Eden.Protocol.Tests.Administration.SkillBonusPresentationTests.LayoutFixtureRendersOneBonusInputAndThreeDistinctPrerequisiteRows'
python tools/Test-SkillEditorLayout.py artifacts/skill-editor-layout
```

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** After restarting the host, review an existing custom mining skill in Bonuses, switch to Advanced and back with a draft, and inspect an expanded prerequisite search in the live administration page.

### Earlier skill-editor performance validation — November 9, 2026

**Automated performance check (2026-11-09):** A fresh-process comparison against the installed 589-skill catalog loaded the same normalized definitions (matching SHA-256) in 8.877 seconds with the previous per-skill loader versus 2.033 seconds with batched loading. Catalog commands fell from 2,947 to 14 and connections from 2,944 to two. Constructing the richer lookup labels took a further 0.535 seconds; three repeated reads plus lookups took 0.053 seconds with no catalog commands or connections. These are one local data-loading comparison, not browser timings or a general latency guarantee. A deterministic 200-added-skill regression bounds cold query/connection counts, and warm reads, history and choices assert zero catalog work. Component checks cover bounded lookup markup, retained unsupported values, escaped text, nullable clearing and draft change tracking. The affected administration/catalog tests and required build/policy/format checks are used; the full .NET suite is not requested.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Open Skills after restarting the server, search and switch several skills, then try named bonus choices and save/discard a draft. Check that opening and closing appearance/bonuses keeps your values and that searching the list retains your draft.

**Stable milestone validation:** Tool restore, solution restore and build passed with zero warnings/errors. The focused filter below passed 39 tests; one optional pinned-client packaging test was skipped because `EDEN_TEST_CLIENT_ROOT` was not supplied for this UI change. Both Python policy suites passed (8 catalog tests and 9 formatting tests), and `tools/Format.ps1 -Check` passed. The final sequence took 39.20 seconds: build 6.57 seconds, focused tests 7.93 seconds, policies 0.93 seconds and formatting 22.91 seconds, plus restore time. No full .NET suite or interactive acceptance was performed.

```text
FullyQualifiedName~Eden.Protocol.Tests.Administration|FullyQualifiedName~Eden.Protocol.Tests.Persistence.CatalogFittingReadTests
```

### Layout and configurable bonus validation — November 9, 2026

**Automated validation:** Tool restore, solution restore and build passed with zero warnings/errors. The final focused filter below passed 65 tests, with one optional pinned-client packaging test skipped because `EDEN_TEST_CLIENT_ROOT` was not configured. Both Python policy suites passed (8 catalog tests and 9 formatting tests), and `tools/Format.ps1 -Check` passed. The stable validation sequence took 37.04 seconds. No full .NET suite was requested or run.

```text
FullyQualifiedName~Eden.Protocol.Tests.Administration|FullyQualifiedName~Eden.Protocol.Tests.Persistence.CatalogFittingReadTests|FullyQualifiedName~Eden.Protocol.Tests.Mining.MiningTests|FullyQualifiedName~Eden.Protocol.Tests.Persistence.PassiveFittingTests
```

Development began through `tools/Test-Feature.ps1 -Filter` with the exact method `FullyQualifiedName=Eden.Protocol.Tests.Administration.SkillBonusEditorTests.MiningRecipePersistsAndAppliesTwentyFivePercentPerLevelToMatchingModulesOnly`. After correcting test fixture setup, that case passed and was expanded into `MiningRecipePersistsAndAppliesConfiguredPercentPerLevelToMatchingModulesOnly`, covering 25%, 12.5% and zero. The final checks cover saved/reopened definitions, projected catalogs, real mining-module yields at levels 0/I/III/V, nonmatching equipment remaining unchanged, shared editable values, unavailable/mismatched recipe rejection, shortened escaped picker captions, read-only original controls, and rendered level previews. Warm editor reads still perform zero catalog commands/connections or fitting calculations. The fixture reproduces package catalog metadata without building or installing a native client package.

**Live acceptance:** Not performed; agent client testing is disabled. Browser layout, keyboard input and native presentation remain unverified.

**Optional maintainer checks:** After restarting the host, open Skills and switch sections with an unsaved draft; inspect long effect choices at a narrow width; create a custom mining bonus and compare its percentage/level preview before saving.

## Publishing and launcher installation

1. Save the skill definitions you want to use. Unsaved form changes block publication.
2. Enter a build-3662168 client folder on the **server computer**, then choose **Build and stage package**. The server requires 64-bit Python 3.11 or newer; `EDEN_PYTHON` can select its executable. `EDEN_CLIENT_ROOT` can supply the initial source folder, and the last successfully used folder is remembered. A prepared source copy with the launcher's verified original-archive backup is also supported.
3. Restart the server normally. The pending catalog and matching client archive activate together before world reconciliation. Saving or packaging alone does not change the active game.
4. Use the updated Eden Launcher. Individual Play and launch groups refresh server information, obtain a ticket for the active package, download changed archive bytes from the authenticated server endpoint and verify size/SHA-256 and the matching signed native manifest before installation. An already-current archive is reused. Close any games using the same installation before changing its package or server trust. Switching to a server without a skill package restores the pinned original archive.

The source installation and base `catalog.sqlite` remain unchanged. The package contains a separate projected catalog plus a client archive with an authored adapter for type/dogma/prerequisite records, localized names/descriptions and Alpha limits. The adapter preserves unchanged native rows, caches its shared views and uses original native functions as fallbacks. Compilation never executes game module bodies. Both the source archive and its Python runtime are pinned by SHA-256. The native manifest signer accepts only the canonical original prefix and substitutes the active server package's exact archive hash; clients cannot request arbitrary code hashes.

Published custom skillbooks with a market group and positive base price become NPC institutional offers. Development markets retain their saved testing prices and stock. Edited base prices affect ordinary institutional skillbook offers; changing only a name does not alter prices. Activation refreshes development market seed markers and retires removed NPC offers while preserving personal orders and execution history. Existing training commitments retain their saved deadlines; changing rank can change the level corresponding to existing skill points.

Back up the stopped world together with `<world>.skills.sqlite`, `<world>.skills-active` and `<world>.skill-packages/`, plus its original catalog. Definitions/history live in the independent authoring database and do not enter resident checkpoint replacement. Packages retain their base catalog fingerprint for configuration compatibility and have their own content identity. A changed original catalog, stale generation, incomplete package or damaged archive is rejected. No game restart or installed-client update is performed by the editor itself.

**Performance** shows session frame latency alongside CPU, memory, authenticated game connections and runtime activity.
**Frame processing time** measures each completed scheduled update for a selected pilot, including server-side
output and mod observation. **Frame start delay** measures how far past its scheduled start the frame began.
These metrics measure individual sessions. For example, a single two-second interval can include frames from several
pilots. The charts show average and peak processing time and peak start delay for each completed interval over
the last two minutes. Time spent idle without scheduled updates is excluded from start delay. An interval without completed frames appears as a gap. Hover over a bar to read its values.
The window is shared across administrators, fills even while the page is closed and resets with the process.

Process CPU usage is normalized across the processors available to Eden. CPU and allocation rates need two samples
and cover the actual elapsed interval between samples. The connection count includes authenticated players at
character selection. Pending logins and web administrator sessions are excluded.

The page refreshes every two seconds while open. A shared, demand-driven cache limits resource sampling to once per two seconds across all administrator viewers. Navigating away disposes that page's refresh loop. The first sample displays collecting indicators for rates. Each refresh checks the current administrator credentials; password rotation stops existing interactive access. The same read-only snapshot is available at authenticated `GET /api/admin/performance` on the administration listener.

Resident hosts also report the last successful checkpoint's generation, completion time, file size and separate owner wait, capture, projection and file-write durations. Failed saves display an alert while retaining the previous successful report. Before the first save, the page says no checkpoint has completed; hosts without a resident world show diagnostics as unavailable. Reading these reports does not acquire world ownership, open SQLite connections, project simulation state, trigger saves or force garbage collection. Frame history is held in memory only.

Authentication uses a separate ASP.NET Core cookie scheme and administrator role. HTTP and interactive operations enforce authorization and the current credential stamp. Forms and writes require antiforgery validation. Password rotation revokes existing HTTP sessions and interactive operation access. Passwords are hashed in a separate `<world>.administrators.json` credential file. Authentication keys live in `<world>.admin-keys` and use the current Windows user's key protection on Windows. These runtime files are excluded from version control. The local password command can rotate credentials while the server runs.

The editor uses Blazor server interaction, following [ASP.NET Core Blazor security](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/?view=aspnetcore-10.0) and [cookie authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie?view=aspnetcore-10.0). It delegates to the same configuration validation and persistence operations as local commands. Installed catalog files and gameplay rules remain outside its ownership.

### Performance panel and dark theme validation — November 7, 2026

**Automated validation:** The initial development run used `tools/Test-Feature.ps1 -Filter 'FullyQualifiedName=Eden.Protocol.Tests.Administration.ServerPerformanceTests.SharedSamplesNormalizeCpuCacheReadersAndTrackAccountRelease'` (one test passed). Stable milestone validation restored tools and the solution, built with zero warnings/errors, passed 23 focused tests, passed both Python policy suites (8 catalog and 9 formatting tests), and passed `tools/Format.ps1 -Check`. The final sequence took 27.40 seconds: build 5.59 seconds, focused tests 5.64 seconds, Python checks 0.97 seconds, formatting verification 14.43 seconds, plus restore time. The full .NET suite was not requested or run.

The final test filter was:

```text
FullyQualifiedName~Eden.Protocol.Tests.Administration|FullyQualifiedName~Eden.Protocol.Tests.Hosting.ConnectionLimitTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting.ConnectionOwnershipTests|FullyQualifiedName~Eden.Protocol.Tests.Hosting.ServerServicesTests
```

Coverage includes shared/monotonic rate sampling, account admission/release, no SQL or fitting work from diagnostic reads, checkpoint failure/recovery, HTTP authorization and credential revocation, and rendered component content. Automated HTML checks establish dark-theme declarations across editor routes; browser appearance and interactive timer behavior remain unverified.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** After restarting the server, open Performance and confirm periodic updates; inspect settings, catalog, revisions and sign-in for dark-theme appearance and keyboard focus.

## Local commands

Stop the server before these commands. They acquire the saved world's ownership file; a running server uses the authenticated local editor. Commands can upgrade an older world but do not activate pending revisions. Run from the repository root, supplying the saved world explicitly:

```powershell
dotnet run --project src/Eden.Server -- --world 'C:\Eden\world.sqlite' --configuration-history
dotnet run --project src/Eden.Server -- --world 'C:\Eden\world.sqlite' --configuration-export 1 --output 'C:\Eden\revision.json'
dotnet run --project src/Eden.Server -- --world 'C:\Eden\world.sqlite' --configuration-import 'C:\Eden\revision.json'
dotnet run --project src/Eden.Server -- --world 'C:\Eden\world.sqlite' --configuration-restore 1
```

History shows the active/pending IDs and recent revisions. Import and restore always create a new pending revision; start the server normally to activate it. A configuration save runs under the existing world owner and transactions, then requires the existing checkpoint mechanism to complete before reporting a durable save. A failed checkpoint retains committed pending state for retry and reports the failure; it does not activate the draft.

## Constant classification

Catalog facts stay in generated models/shared definitions. Native service IDs, effect operations, tax formulas, eligibility rules and mathematical invariants remain named code values. The table above lists server policy knobs. Settings never enable unsupported items or alter the build-specific support policy. Existing economic tables remain the transaction-owned projections used by features, while immutable revision documents own administration history.

## Autonomous population

Open **Player bots**, make changes, and select **Save for restart**. The **Population** page shows the active
settings, occupied slots, saved/paused totals, individual system access and waiting reasons.

- **Bot population** contains the master switch, the world-wide **Active bot cap**
  (`settings.capsuleers.activePilotCap`) and the switch for preparing unvisited systems in fresh universes
  (`settings.capsuleers.prepareUnvisitedSystems`). The cap includes pilots in idle or offscreen work and watched-gate visitors.
  **0** means unlimited. Turning off the master switch pauses and hides all simulated pilots.
- **Where bots work** applies the default, then the region override, then the solar-system override
  (`settings.capsuleers.systemAccess`). To keep work out of a starter system, choose its region and solar system and
  set the system to **Transit only**. Bots may still cross it by stargate. Choose **Use region rule** to remove the
  system override. Restricted home communities pause, and saved assignments that require a restricted work
  destination wait with their checkpoints intact.
- **Mining** controls mining fleets and threat awareness.
- **Hauling & deliveries** controls dedicated agent couriers in fresh universes (`settings.capsuleers.freshCourierPilots`)
  and new trade-good shipments on civilian routes (`settings.capsuleers.tradeGoodsFreight`). Disabling shipments
  preserves existing cargo and normal market supply jobs. The freight settings set regional hull variety,
  ordinary/courier budget ranges in ISK, courier frequency, empty return trips, cargo type IDs and wreck survival.
  [Shipment defaults and cargo type rules](capsuleer-population.md#hauler-freight)
- **PvE combat & salvage** controls belt ratting, ordinary anomalies, wreck salvage and dedicated fresh-universe
  bounty hunters (`settings.capsuleers.freshBountyPilots`). Dedicated hunters require belt ratting. Disabling salvage
  stops a pending cycle when the pilot next runs. Ordinary wreck looting can still occur.
- **Exploration** (`settings.capsuleers.exploration`) lets eligible bots acquire exploration equipment and scan and hack
  high-security pirate data and relic sites. Disabling it sends active explorers back to their departure stations.
- **Persistent roaming gangs** (`settings.capsuleers.roamingGangs`, default on) permits coordinated nullsec hunting with
  existing ships. **Roaming gang size** (`settings.capsuleers.roamingGangSize`) accepts 3–6, default 4. The existing roam
  chance controls formation. [Gang behavior and requirements](capsuleer-population.md#persistent-roaming-gangs).
- **Regional conflicts** (`settings.capsuleers.regionalConflicts`, default on) assigns existing gangs to regional raids,
  interception, owned freight escorts and witnessed defensive responses. **Regional operations per group**
  (`settings.capsuleers.regionalOperationsPerGroup`) accepts 1-3, default 1. Losses delay offensive launches; no replacement
  ships are granted. Population reports groups and outcomes. [Regional behavior](capsuleer-population.md#regional-conflicts).
- **Corporations, alliances & sovereignty** contains three independent switches.
  - **Automatic corporation formation and recruitment** (`settings.capsuleers.automaticRecruitment`) controls new bot-led
    corporations, advertisements, applications initiated by bots and automatic offers to human applicants.
    Existing bot applications and relocations continue through their normal workflow.
  - **Form and grow bot alliances** (`settings.capsuleers.automaticAlliances`) controls new alliances and automatic
    admissions initiated by bot alliances. Accepted applications and existing dues continue.
  - **Start new sovereignty claims** (`settings.capsuleers.newSovereigntyClaims`) controls new hub bids and first-claim
    departures. Existing buy orders, underway expeditions and defense of claimed systems continue.
- Expand **Ambient traffic** to adjust the established-world station roster or the gate visitor pool. Station traffic
  accepts **0–10×**, default **1×**, up to 60 travelers per community. Fractional densities use a saved-seed draw.
  Watched-gate visitors accept **0–16**, default **4**. Both ends of a gate share the same saved visitor identities.

All edits use the ordinary pending revision, comparison, history, export/import and restart workflow. Lower limits hide excess
pilots from space, Local, station guests and directional scans and exclude them from scheduled work. No pilot, ship, cargo,
equipment, wallet, loss, grant record or saved activity is deleted. Raising a limit reactivates the eligible saved roster slots
before missing station-traffic/miner slots are prepared in entered regions. New gate visitors are prepared on gate watches. A lost ship remains lost.
Activity deadlines shift by the paused interval; suspended pilots earn no catch-up actions, items or rewards.
Repeated restarts preserve the original pause and never replay a supply or wallet grant.
Normal startup retains saved pilots and starts preparation when a human selects a character. Occupied systems get priority.
With fresh-universe background preparation enabled, Eden also prepares eligible unvisited high-security station
systems in bounded batches. It respects the active bot cap and work access rules, and preparation stops when the last
player leaves. Established worlds prepare new communities on demand in occupied systems.
Saved identities and possessions are reused, and dormant time produces no catch-up rewards. Completed catalog/settings/snapshot
fingerprints are reused; interrupted preparation retries missing communities
without replacing completed pilots. Unsupported or temporarily depleted communities are recorded
as deferred rather than receiving invented fits or replacement supplies. Offline configuration export/import and account
administration commands do not populate the galaxy.


Defaults preserve the existing population policy. Older revision documents receive the new defaults automatically;
`Capsuleers.GlobalProvisioning` remains a preserved legacy preference and is not the population switch.
See the [population guide](capsuleer-population.md) and [implementation record](protocol/capsuleers.md#configurable-simulated-pilot-population--november-9-2026).

### Planned administration controls

Live pause and resume remains planned. It requires preserving remaining activity deadlines across the schedulers
and persisting the pause across restart.
