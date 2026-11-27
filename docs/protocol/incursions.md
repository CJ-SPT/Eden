# Sansha Incursions

## Pinned headquarters dungeon profile

Build 3662168 tags True Power Provisional Headquarters (`client/dungeons` row `5114`) with catalog faction `500021`
(Unknown), archetype `63` and entry object `1009453`. Eden validates that faction for this dungeon and uses Sansha's Nation
for the encounter and beacon owner. The source row SHA-256 is
`dfab5232c0b0d767904e6c7df728c872d9299a11df378982fec4c616f22f59a2`. EVE.js
`server/src/services/incursion/incursionLifecycleContract.js` records the same faction for this ID.

Updating the test fixture to the pinned values reproduced the startup exception on revision `99cb8d27`. After the fix,
`AllVariantsUsePinnedArchetypesAndLowSecuritySelectsUroborus` loaded all 15 sites from the installed `catalog.sqlite`
through `EDEN_TEST_INCURSION_CATALOG` in 4.39 seconds including test runner startup. The focused Incursion, Agency and
world setup run passed 66 tests in 37 seconds of test execution.

[Documentation index](../README.md) · [Protocol records](../protocol.md)

## Incursion loyalty point journal

Journal → Incursions → Loyalty Point Log shows the selected pilot's pooled CONCORD LP awards and campaign outcomes.
Each eligible site completion records its LP, completion time, site, system and number of rewarded pilots. Defeating the
mothership records one paid row per recipient alongside the wallet credit. If Sansha withdraw, the journal records the lost
pool at the focus's expiry. The native loss row displays the loss label and zero paid LP. The save retains the forfeited
amount. Rows survive checkpoints and campaign rotation.

The pinned build **3662168** `journal.pyj` calls `rewardMgr.GetRewardLPLogs()` with zero arguments and reads a list of named
rows. `appLogConst.pyj` defines stored **312**, paid **313** and lost **314**. The pinned `grouprewards` catalog supplies the
site-class labels. EVE.js **261108.1** was checked against its reward manager and the stored, paid and forfeited journal rows.
[CCP's Journal changes](https://www.eveonline.com/news/view/updates-to-the-agency-journal-and-agent-finder) ·
[EVE University Incursions](https://wiki.eveuniversity.org/Incursion)

| Local EVE.js reference | SHA-256 |
| --- | --- |
| server/src/services/rewards/rewardMgrService.js | f101a6575796818d2729c725c726b2c26641a35e848a72f98dea7877edcb4a42 |
| server/src/services/incursion/incursionRewards.js | d421e68bb53ff327ca05d791508e7cbe5b8638362f76300f3ee95da4baa29e67 |

World schema **233** adds private history keyed by pilot and source. Each site or terminal row commits with the existing
resident reward change. Existing held LP retains its pending balance and generates a terminal row on settlement. Earlier
site awards carry only the data retained by the existing live pool, so per-site journal entries begin after the upgrade.

### Automated validation

The exact `IncursionCampaignTests.SitePayoutAndDelayedLoyaltySurviveCheckpointAndSettleOnlyOnVictory` filter passed both
cases. The victory case took 0.93 seconds and the forfeiture case took 0.58 seconds. They cover the native LP journal reply,
history privacy, stored and terminal amounts, checkpoint reopen and rollover.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

- Open the Loyalty Point Log after clearing a site and again after the focus ends. Compare the history with the CONCORD LP wallet.

## Roaming Sansha populations

While a focus is active, Sansha patrols take over ordinary asteroid belts and active ice fields in its systems
across all three security bands. Ordinary belts get a Tama Cerebellum and a Romi Thalamus. Ice fields get an
additional Romi. The patrols use the existing NPC combat and electronic warfare behavior. The ore and ice remain
available, and empire guards, mining fleets and scripted encounter populations keep their own schedules.

Lowsec and nullsec systems get one camp wherever the system has eligible stargates. Each camp brings a
Schmaeel Medulla, a Tama Cerebellum and a Romi Thalamus. Each ten-minute shift assembles a new camp at the next gate in a
stable order per system and focus. Clearing a camp keeps the gate clear until the next shift. A belt patrol that
is completely destroyed can return after the normal ten-minute belt delay. Ambient kills yield no bounty or
Incursion site credit. The exact group sizes, gate rotation and timing are Eden rules.

On early victory or expiry, ambient NPCs withdraw and the ordinary belt population resumes under its normal
seeder. Camps and patrols share the existing NPC contact and combat paths. Their saved rows carry their exact
expiry times and survive checkpoint replay. World schema **226** adds the optional NPC expiry. If a campaign
system has no asteroid belts, only its eligible ice fields and stargates can host ambient groups.

### Population source evidence

CCP describes replacing celestial pirates while keeping CONCORD and empire navies. The EVE University account
identifies the lowsec and nullsec gate-camp rule and ice-field replacement. In the pinned **3662168** catalog,
Schmaeel (2907) and Tama (2939) supply warp disruption and Romi (2859) supplies cruiser firepower. None has
an entity kill bounty. The local EVE.js belt runtime has regional populations and its Incursion implementation
covers site gates. The ambient extensions use Eden's resident population scheduler and existing wire shapes.
[CCP Incursion design](https://www.eveonline.com/news/view/the-nation-strikes-back-1) ·
[EVE University Incursions](https://wiki.eveuniversity.org/Incursions#Effects_of_Incursions).

| Local EVE.js reference | SHA-256 |
| --- | --- |
| server/src/space/npc/beltRatRuntime.js | 82f911de0bc228f8d9f00f1d43fa94f28260fa8f43efa27833eb6d1f2a773c78 |
| server/src/space/asteroids/naturalBeltAuthority.js | 1f3934873e619db6f781dfd2a04dd65d23575dc9976dbe3fbf05dee632fc9d81 |

### Population automated validation

The solution built with zero warnings and errors. The final targeted run passed **19 of 19** cases in **17.82
seconds**. Its filters selected Incursion ambient populations, population deadline allocation, camp tackle and
NPC missile expiry, and the schema-225 upgrade. Coverage includes the warm and fallback population paths,
same-tick mothership victory, checkpoint recovery, and belt and camp cooldowns.

The broader focused run passed **276 of 277** cases in **121.11 seconds**. It covered Incursion, NPC population,
encounter, combat, electronic warfare and missile tests, simulation and system isolation, world migration,
gate activity and retired NPC cleanup. The resident pirate case in `NpcPopulationTests.KillRewardsUseRoleAndActualCorporation`
failed identically on the unchanged base `febf4c8c`. Catalog setup (8 cases), formatting policy (9 cases),
typed catalog access, documentation links and `tools/Format.ps1 -Check` passed.

### Population live acceptance

Not performed; agent client testing is disabled.

### Population optional maintainer checks

- Visit a lowsec gate before and after a camp shift and verify the Sansha warp disruption indicators.
- Visit an active belt and recheck it after the mothership falls to confirm the regional rats return.

## Security campaigns and system effects

The Agency and star map show up to **five active focuses**. Eden rotates one highsec focus, one lowsec focus and
three nullsec focuses. Each has its own sites, influence, victory and CONCORD LP pool. All use the established
three-day active window and four-day UTC schedule. Lanes rotate independently through observed constellations of
their security band. A victory closes only that focus and pays only its pool.

Eight regular clears reduce a focus to zero Sansha influence. A Headquarters clear is also required before the
final site can appear. Lowsec additionally requires **24 hours** since opening. Highsec and nullsec use the
Kundalini Manifest. Lowsec uses Uroborus. The reward tables use the site's own security band and the
qualifying fleet size.

| Security | Eligible constellations in the rotation |
| --- | --- |
| Highsec | Amdimmah, Ahrnot, Asalola |
| Lowsec | Aeman, Afinoo, Akar |
| Nullsec | 0-8MWQ, 0-YMZM, 02QH-A, 04-LOF |

### Remaining site variants

The pinned site IDs below provide beacon identity, archetype and gate admission. Eden uses its own combat rooms,
NPC waves and objective rules. Existing Vanguard, Scout, Assault and Headquarters sites retain their saved identities.
Regular variants can run concurrently in their assigned systems. Each replenishes on its own replacement deadline.

| Tier | Playable encounter identities and pinned dungeon IDs |
| --- | --- |
| Scout | Distress Beacon (5193), Forward Reconnaissance Outpost (5188), Propaganda Cluster (5190), Industrial Proxy (5191) |
| Vanguard | Nation Commander Outpost (5166), Nation Mining Colony (5165), Override Transfer Array (5164) |
| Assault | Nation Commander Stronghold (5168), Overwhelmed Civilian Facility (5167), Nation Consolidation Network (5172) |
| Headquarters | Nation Rebirth Facility (5171), True Power Provisional Headquarters (5114), True Creations Research Center (5175) |
| Final | The Kundalini Manifest (5158), Uroborus (5201) |

Industrial Proxy's local beacon is at a Staging asteroid belt when one exists. Nation Mining Colony requires **255
Lyavite** after the defense waves and provides a mineable deposit. Overwhelmed Civilian Facility requires delivery
of **40 captured civilians** to the freighter. True Creations Research Center uses **one CONCORD MTAC** at the shield
transfer control tower to expose the final targets. Cargo pickup and delivery use the normal inventory controls.
Override Transfer Array's optional data-analyzer hack cancels the opening reinforcement timer so the fleet can clear
the first wave before the next arrives. Nation Consolidation Network's eight gates connect two hull-restricted lanes,
and both wings need to clear each stage. True Power Provisional Headquarters unlocks its two interior gates in sequence.

### Flagship cargo

A winning group earns one wreck at the flagship's position. It grants one to three distinct True Sansha modules
and one single-run Sansha blueprint copy. Succubus, Phantasm, Nightmare and Shadow can drop in each security band.
Uroborus has an Eden-configured **5%** chance to substitute a Revenant copy. The top eligible damage contributor in
the winning group receives ownership under normal fleet loot rights. The wreck inventory and the focus settlement
commit together. Reopening a completed campaign cannot duplicate a wreck or award.

### Constellation effects

| Effect | During an active focus |
| --- | --- |
| NPC bounty | Ordinary NPC bounties pay half in affected systems, including Staging. |
| Cynosural fields | Standard cyno ignition is jammed. Covert cynos and previously lit fields retain their own rules. |
| Ship weapons | Turret, missile, combat drone and smartbomb damage is reduced according to scene and Sansha influence. |
| Shield and armor | Damage resonance rises according to the same scene and influence scale. Hull resistance keeps its base value. |

Vanguard, Assault and Headquarters use **10%**, **25%** and **50%** at full Sansha influence. The scalable effects
reach zero at zero influence. Staging uses the bounty and cyno effects. The resident system index supplies both
gameplay and the client status. On entry the pinned client receives the current scene's effect descriptors, and an
influence update refreshes the progress bar. Expiry, victory and departure remove the local status.

The outgoing damage modifier covers regular and precursor turrets, non-Breacher ship missiles, normal combat drones
and smartbombs. Eden retains existing damage rules for Vorton projectors, fighter abilities, Breachers, doomsdays and
launched bombs. [Roaming Sansha populations](#roaming-sansha-populations) cover the systems' ambient gates and belts.

World schema **225** saves site objective receipts with the encounter generation. The highsec tale IDs from older
saves still refer to the same rotation. Lowsec and nullsec have disjoint tale ID ranges. Checkpoints retain each focus's
content, influence, loyalty and combat state.

### Source evidence

The constellation assignments and security-specific flagships were checked against the pinned build **3662168** catalog
and observed roles. Retail loot probabilities are unpublished. Eden uses an explicit deterministic drop rule.
[EVE University constellation layouts](https://wiki.eveuniversity.org/Constellation_layouts_for_Incursions) ·
[EVE University Incursion sites and loot](https://wiki.eveuniversity.org/Incursion_sites) ·
[CCP Incursion system effects](https://www.eveonline.com/news/view/the-nation-strikes-back-1).

Pinned client examination reused the original inert REA archive extraction. The local HUD and Agency read effect
dictionaries with `name`, `texturePath`, `hint` and `isScalable`. `OnTaleData`, `OnInfluenceUpdate` and `OnTaleRemove`
drive the local status. Label IDs are **239122** (damage), **239123** (tanking), **239124** (cyno) and **239134** (bounty).
The local EVE.js reference supplied behavioral comparisons for phase placement, site mechanics and effect metadata.
The reference checkout has no Git metadata. The SHA-256 hashes of the inspected effect contract and site mechanics
are listed here so the specific revisions can be identified.

| Reference file | SHA-256 |
| --- | --- |
| incursionSystemEffectContract.js | 9d1d9ae8c61b71bc5711bc662cf3c36dd38700c294292a3fd64501db3c9e766b |
| incursionSystemEffects.js | 8cbc1fe1599af5ff98ca1c4d4dc81ff94ce613dcd0a82761307e8a88fc24b0dc |
| incursionSiteMechanics.js | 3e0bf90eddd2b93adf9f03b30cc006810fe902dd49d62da3897ef22834d17c43 |

The pinned catalog assigns damage and tanking effects to source types **3069**, **3493** and **3494**. Dogma effect
**4728** has no generic modifier payload. The resident simulation applies the pinned profile at the weapon and
defense boundaries. Historical bounty and cyno evidence is linked above.

### Expansion automated validation

The solution built with **zero warnings and errors**. The exact security and loot methods verified five concurrent
focuses, security-based payouts, the lowsec final deadline, isolated LP settlement, faction loot and blueprint
transfer after checkpoint replay. The exact Override Transfer Array method exercised target lock, analyzer
activation and hack completion. The analyzer test passed in **5 seconds** using the pinned signature radius.

The focused dependency run passed **237 of 237** cases in **122.99 seconds** after a **7.52-second** build. The
`FullyQualifiedName` filters covered `Exploration.Incursion`, `Exploration.AgencyTests`,
`Exploration.CombatAnomalyWorldTests`, `Exploration.HackingWorldTests`, `Combat.NpcCombatTests`,
`Combat.NpcMissileTests`, `Combat.ResidentNpcMissileTests`, `Combat.PilotCombatTests`, `Combat.PilotDroneTests`,
`Combat.SmartbombTests` and `CapitalTravelTests.IncursionJamsStandardCyno`. Catalog setup (8 cases), formatting policy
(9 cases), typed catalog access policy and `tools/Format.ps1 -Check` passed. Validation used resident fixtures and a
read-only inspection of the pinned catalog. It did not launch the client or the full .NET suite.

### Expansion live acceptance

Not performed; agent client testing is disabled.

### Expansion optional maintainer checks

- Check the Agency for simultaneous focuses and inspect how the local effect icons change between Staging and a combat system.
- Run a cargo or gate objective in one of the newly available sites.
- Clear Uroborus, then check the flagship wreck and the focus's CONCORD LP settlement.

## High-security Incursion campaign

Find the current focus in **Agency → Incursions** or the star map. Warp to a site beacon in the listed system.
The Scout site opens directly on grid. The other sites use an acceleration gate and its pinned ship restrictions.
Eden runs one regular site in each affected system, with a ten-minute replacement delay after a clear.

| Site tier | Playable site | System role | Encounter |
| --- | --- | --- | --- |
| Scout | Distress Beacon | Staging | Fight two pursuit groups at the beacon. |
| Vanguard | Nation Commander Outpost | Vanguard | Clear four waves. |
| Assault | Nation Commander Stronghold | Assault | Destroy the trigger wings to advance, then clear the surviving ships. |
| Headquarters | Nation Rebirth Facility | Headquarters | Engage overlapping waves and timed reinforcements. |
| Mothership | The Kundalini Manifest | Headquarters | Defeat the escorts and the Sansha flagship to end the focus. |

The rotation still uses three-day focus windows and a four-day UTC schedule. The Assault and Headquarters systems
are Gidali and Molea in Amdimmah, Yooh and Hostakoh in Ahrnot, and Oipo and Haajinen in Asalola. Eight regular clears
reduce Sansha influence to zero. A Headquarters clear is also required for the mothership beacon to appear alongside the
regular Headquarters site. Destroying the mothership and clearing its encounter releases the campaign's CONCORD LP,
including past awards to offline pilots. When the focus expires, remaining sites retire and the unpaid pool expires.

An eligible on-grid pilot earns a share of a site's payout by dealing damage or repairing a fleetmate's Incursion damage.
The server credits the actual shield, armor or hull HP restored, capped by outstanding damage from that site on the
recipient's current hull. Overheal, self-repair, and repairs to ships outside the pilot's fleet earn zero contribution.
Paid remote repair modules and repair drones use the same contribution rules. The group with the greatest qualifying ship
damage wins. Qualifying logistics pilots in the winning fleet count toward the size bracket. The server uses each site's
pinned high-security ISK and LP brackets. ISK pays immediately and LP accrues to the campaign pool.

### Protocol, catalog and save records

| Tier | Pinned dungeon ID | Site archetype | Reward group |
| --- | --- | --- | --- |
| Scout | 5193 | 60 | 8 |
| Vanguard | 5166 | 61 | 1 |
| Assault | 5168 | 62 | 2 |
| Headquarters | 5171 | 63 | 3 |
| Mothership | 5158 | 64 | 10 |

The pinned campaign scenes are Headquarters **1**, Assault **2**, Vanguard **3** and Staging **4**. The mothership
advertises through the existing HQ system row and the report's native `hasFinalEncounter` flag. Local site cards
read their type from the beacon's slim `archetypeID`. The Agency's existing `OnInfluenceUpdate` listener refreshes
cached content after a campaign change, including unlock, expiry, victory or a new focus. The session sends site contacts
before the update. An already open individual constellation page refreshes when reopened. The monitor samples a shared
publication with no per-session catalog read.

World schema **224** retains Vanguard site identities, campaign progress and pending LP from schema 223.
A focus already repelled under schema 223 remains closed. The new state checkpoints Headquarters qualification,
final defeat, repair contributions and the remaining repairable Incursion damage. Ship and site identity scope the repair
ledger. Site completion, payment and campaign progress commit together. The server still uses Eden-authored
combat layouts. This campaign covers one playable encounter per high-security tier and the high-security mothership.
Other security bands, the remaining encounter variants, mothership loot and constellation-wide combat effects are outside its scope.

The pinned reward records are in `client/grouprewards`. Scout group **8** uses a security-independent curve.
Groups **2**, **3** and **10** use high-security curves.

The current campaign reuses the pinned-client evidence in the earlier Vanguard milestone. The additional
`client/dungeons` record SHA-256 fingerprints are listed below. Eden reads those records for identity and gate
admission and builds its own objectives and combat layouts.

| Dungeon | SHA-256 |
| --- | --- |
| 5193 | `43c06eb7c33c1772caa7ca83877ff69f181a539e230cb7ac7ac60e3addef0e28` |
| 5168 | `297d9f31cef53387c84106b9d25938cb392824a724f2c45a814456fd799253ed` |
| 5171 | `a719563d71b1fd04fb027c7c65db14faa6f742efdd5cf34b036646cac7e38b7d` |
| 5158 | `bbdecabd01e9c0a0ce5407f51ea1d5d1d181cf0218a33c614f9472b463ad6896` |

The local EVE.js reference was checked for final-site availability, terminal settlement and contribution.
Its `incursionFinalSiteRuntime.js` SHA-256 is `aee57c8a38fa8fd8f9a49d6dfa17bf66daacfc3bc119c5e720243ee1d97d84e9`.
Its `incursionSiteMechanics.js` SHA-256 is `3e0bf90eddd2b93adf9f03b30cc006810fe902dd49d62da3897ef22834d17c43`.

CCP and EVE University describe the influence and Headquarters requirements,
fleet payouts and logistics qualification. [Incursion sites](https://wiki.eveuniversity.org/Incursion_sites) ·
[CCP logistics qualification](https://www.eveonline.com/news/view/patch-notes-for-august-2017-release) ·
[CCP repair and combat qualification](https://www.eveonline.com/news/view/patch-notes-for-february-2020-release).

### Campaign automated validation

The final solution build passed in 14.35 seconds with zero warnings and errors. Both exact campaign startup cases
passed with the production gameplay SQLite guard enabled. The integrated run completed **357 cases in 195.66 seconds**
and passed **356**, including every Incursion and Agency test.

The `FullyQualifiedName` filters selected the Incursion and Agency tests, `CombatAnomalyWorldTests`,
`CombatAnomalyLayoutTests`, `RemoteRepairTests`, `AncillaryRemoteRepairTests`, `MutadaptiveRemoteRepairTests`,
`UtilityDroneTests`, `NpcCombatTests`, `NpcMissileTests`, `ResidentNpcMissileTests` and `CharacterLogoffTests`.
The one failure, `MutadaptiveRemoteRepairTests.RestrictionsAndDuplicateFittingPreserveInventory`, also failed in isolation
at the same hull assembly assertion on unchanged main `09a1e574`.

The new tests cover all site tiers, held LP through upgrade and restart, mothership unlock and victory, expiry,
campaign publication and effective module and drone repair payouts. Catalog setup and formatting policy passed
**8** and **9** cases, respectively. The catalog runtime access policy, `tools/Format.ps1 -Check`, documentation links
and `git diff --check` passed. Local TRX reports are under `artifacts/incursion-campaign-20261124`.

### Campaign live acceptance

Not performed; agent client testing is disabled.

### Campaign optional maintainer checks

- Open the Agency during a focus and check each tier's beacon after traveling to its system.
- Run a site with a damage pilot and a shield or armor logistics pilot and inspect both payout entries.
- Clear eight sites including Headquarters, then defeat the mothership and observe the Agency and CONCORD LP balance.


## High-security Vanguard milestone

### Find and run an outpost

Open the Agency's **Incursions** view or the star map's Incursion filter to find an active high-security focus.
Travel to one of the listed Vanguard systems. Warp to the **Nation Commander Outpost** and activate its acceleration gate
in an eligible hull. Each outpost uses an Eden-authored layout and is shared by pilots in the system. Clear every ship in a wave to bring in the next one.
The third wave includes Sansha's Nation Commander. The fourth clear completes the site.

### Focus rotation and completion

Eden rotates among three observed high-security constellations on a four-day UTC schedule. Each focus lasts up to **72 hours**,
followed by **24 hours** before the next scheduled focus. An early victory ends the active focus and leaves the next opening
on its original schedule. Each Vanguard system has one concurrent outpost, with a **10-minute** replacement deadline after
a clear while the focus remains active.

| Constellation | Staging system | Vanguard systems |
| --- | --- | --- |
| Amdimmah (`20000566`) | Moniyyuku (`30003881`) | Palas (`30003878`), Reteka (`30003880`), Safshela (`30003879`) |
| Ahrnot (`20000604`) | Kasi (`30004134`) | Hilmar (`30004133`), Jeshideh (`30004132`), Shura (`30004135`) |
| Asalola (`20000209`) | Torrinos (`30001429`) | Ibura (`30001428`), Isinokka (`30001426`), Yoma (`30001427`) |

Each site clear removes one eighth of the focus's initial Sansha influence. The eighth clear repels the incursion and
releases its pooled **CONCORD LP**. Expiry forfeits the pool. Site completion pays ISK immediately to eligible contributing
pilots still on the combat grid. The server groups qualifying ship damage contributors by fleet membership when the site clears and pays
the highest-damage group. The pinned high-security
Vanguard table pays each of **5–10 qualifying pilots** **10,395,000 ISK** and adds **1,400 LP** to each participant's pool.
Smaller and larger groups use the corresponding catalog tiers. Outpost NPCs carry zero bounty and drop no loot.

Campaign progress, site generations, damage contributions, pooled LP and durable rewards use resident state and normal world
checkpoints. World schema **223** saves the campaign and contribution rows. The schedule and site replacement use absolute deadlines. This milestone covers high-security Vanguard combat.
Other security bands, Scout, Assault and Headquarters sites, a mothership encounter, logistics qualification and
constellation-wide combat penalties remain outside its scope. The Agency can cache the report within a session. Live tale and influence
notifications, the local Incursion HUD and LP history are outside this milestone.

### Client and catalog evidence

Inert REA archive extraction and xdis inspection used client **3662168**, archive SHA-256
`37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368`. The
[original Agency directory record](agency.md#native-agency-directories--november-10-2026) documents the previously empty map routes.

| Client consumer | Native contract |
| --- | --- |
| `ContentProviderIncursions.GetAllIncursions` | Calls `map.GetIncursionGlobalReport()` and sorts the returned `KeyVal` entries by `stagingSolarSystemID`. The card reads `aggressorFactionID`, `state`, `influence` and `hasFinalEncounter`. |
| `IncursionContentPiece.GetSceneTypeBySolarSystemID` | Calls `map.GetSystemsInIncursions()` and reads the rowset's `locationID` and `sceneType`. The map filter also uses `templateNameID`, set to the pinned Sansha Incursion label **507300**. `talecommon` defines Vanguard scene **3**, Staging scene **4** and Incursion template class **2**. |
| `IncursionSystemContentPiece.GetAllIncursionSites` | Looks up the current ballpark's slim records by `archetypeID`. The Vanguard archetype is **61**. The site card reads `dungeonID`, `dungeonNameID`, the slim item ID and owner ID. |
| Journal `ShowIncursionTab` | Reads the report's `influence`, `lastUpdated`, `graceTime` and `decayRate`. Calls `rewardMgr.GetDelayedRewardsByGroupIDs` with a tuple of report `rewardGroupID` values, then reads the first reward's `rewardQuantity` for the matching group. Eden uses the tale ID as the reward group and returns the selected pilot's held LP. |

REA extraction evidence `ev_4a0b7ea04b5b3de584e295ab215bbfae31f14cddf3fe4f22cbfc4d54079d5e3f`,
`ev_3e65aa2b5df12a5b6c8ee6ccac309379b77260d6598ab95efba00ad47421a2ad`.

| Pinned catalog record | Evidence |
| --- | --- |
| `client/dungeons` **5166** | Nation Commander Outpost, archetype **61**, name message **114106**, Sansha faction **500019**, entry beacon type **10645** and acceleration gate type **17831**. Lobby **23353** connects to combat room **23352** under ship list **486**. Eden uses the native identity and eligibility metadata with its own placement and wave composition. |
| `client/grouprewards` **1** | Vanguard count breakpoints, immediate ISK, delayed LP and issuer **1000125** (CONCORD). Amounts use the last count breakpoint reached. |
| `client/accountingentrytypes` **99** | Corporate Reward Payout. The wallet journal attributes the site award to CONCORD. |
| `client/systems` and solar-system and constellation records | System identities, positions and high-security classification for the three focus layouts. |

The pinned raw catalog record SHA-256 fingerprints are `254806a502e1d9b0a395760ac3bdbd896901eac71784bb11f302740b61a356cd`
for dungeon **5166** and `0204b3c51204a7034d8c2faf0e3c8222eadae8b5f87368e6cba348ee95087f12` for reward group **1**.

The local EVE.js reference snapshot has no Git metadata. SHA-256 fingerprints identify the inspected source.

| EVE.js source | SHA-256 |
| --- | --- |
| `server/src/services/incursion/incursionPayloads.js` | `00cc56e094cb86f98d2a9d2f76b337b848ac9c2696d068d332368d2174b2aaac` |
| `server/src/services/incursion/incursionLifecycleContract.js` | `60af37b385dc32045b62d77ab9eee9dfbe681d839a8753c3845170ecb029bed9` |
| `server/src/services/incursion/incursionParticipation.js` | `30c044369694072483a6494138df56d6d23de0d120793fc764bcdd5d8404fb49` |
| `server/src/services/incursion/incursionRewards.js` | `d421e68bb53ff327ca05d791508e7cbe5b8638362f76300f3ee95da4baa29e67` |

CCP describes site rewards, the final-boss LP release and payout qualification through damage or logistics.
Eden's rotation window, site quota, respawn deadline, wave counts and eight-clear victory are local rules for this milestone.
[CCP's Incursion design](https://www.eveonline.com/news/view/the-nation-strikes-back-1),
[2017 payout qualification](https://www.eveonline.com/news/view/patch-notes-for-august-2017-release),
[2020 qualification update](https://www.eveonline.com/news/view/patch-notes-for-february-2020-release).

Community observations identify the site roles for these constellations and the species in the four waves.
[EVE University constellation layouts](https://wiki.eveuniversity.org/Constellation_layouts_for_Incursions#Constellations_in_high_security_space),
[Nation Commander Outpost](https://wiki.eveuniversity.org/Nation_Commander_Outpost),
[Vanguard fleet cribsheet](https://wiki.eveuniversity.org/FC_VG_Cribsheet).

### Automated validation

The final solution build passed in 11.47 seconds with zero warnings and zero errors. The final focused run passed
**81 cases in 53.15 seconds**, using a `FullyQualifiedName` class filter for `IncursionCampaignTests`, `IncursionSiteTests`,
`AgencyTests`, `CombatAnomalyLayoutTests`, `GateActivityProtocolTests` and `TypedServiceModelsTests`. It covers native discovery
and warps, gate admission, all four waves, off-grid payout rejection, wallet journal ownership, checkpoint and restart,
LP release on victory, LP forfeiture on expiry, and NPC retirement in another warmed system.

The earlier adjacent run passed **174 cases in 67 seconds**, before the cross-system retirement publication change.
Its filter selected `AgencyTests`, `CombatAnomalyWorldTests`, `CombatAnomalyLayoutTests`, `NpcCombatTests`, `PilotDroneTests`,
`GateActivityProtocolTests` and `TypedServiceModelsTests`. The catalog setup and formatting policy scripts passed **8** and
**9** cases, respectively. `tools/Format.ps1 -Check`, documentation link and whitespace checks, and `git diff --check` passed.
Local TRX reports are under `artifacts/incursion-vanguard-20261124`.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

- During an active focus, open the Agency and star map and verify the staging and Vanguard locations.
- Warp to an outpost and verify its acceleration gate, four waves and wallet award on the final clear.
- Clear the focus's eighth site and verify the CONCORD LP balance and removal of the focus from discovery.
