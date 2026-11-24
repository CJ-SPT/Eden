# Sansha Incursions

[Documentation index](../README.md) · [Protocol records](../protocol.md)

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
