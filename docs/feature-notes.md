# Feature implementation notes

[Documentation index](README.md) / [Protocol records](protocol.md)

## Gameplay overview

- **Flight and combat.** Fit a ship, join a fleet, take agent missions, and fight NPCs or other pilots.
  [Fitting and modules](#fitting-weapons-and-modules) · [Supported missions](missions.md)
- **Exploration.** Scan and hack data and relic sites, explore wormholes with mass and lifetime limits, and run Abyssal pockets.
  [Scanning and hacking](protocol/information.md) · [Wormholes](protocol/wormholes.md) · [Abyssal Deadspace](protocol/abyssal.md)
- **Industry and trade.** Mine resources, operate planetary colonies and moon refineries, run blueprint and production jobs,
  and trade under the world's market preset. [Industry](#mining-industry-and-economy) · [Planetary industry](protocol/planetary.md)
- **Fleets and territory.** Form fleets, manage corporations and alliances, deploy Upwell structures, and contest sovereignty.
  [Fleets](#cooperative-fleets) · [Corporations](corporations.md) · [Structures](protocol/structures.md) · [Sovereignty](protocol/sovereignty.md)
- **Faction Warfare.** Enlist a pilot, corporation or alliance. Run militia missions, earn ranks and LP, capture complexes and
  battlefields, and contest infrastructure hubs in both warzones. Optional militia bots select reachable Frontlines and
  contest shared complexes. [Faction Warfare](protocol/faction-warfare.md#dynamic-faction-warfare-patrols)
- **Simulated capsuleers.** Encounter pilots that mine, haul, fight, explore and salvage. On established worlds, eligible
  bots buy and fit separate PvP ships. The server funds bot refits by default. Corporate pilots can take paid production work,
  replace lost ships and fund first sovereignty claims. Bot work continues with nobody logged in by default. With background
  preparation enabled, eligible high-security systems gain bots ahead of player visits. The default departure policy favors
  PvP roams for eligible independent pilots heading into nullsec. [Activity-funded PvP](protocol/capsuleers.md#activity-funded-pvp-ships) ·
  [Independent nullsec expeditions](capsuleer-population.md#independent-nullsec-expeditions) ·
  [Capsuleer encounters](capsuleer-population.md) · [Population settings](world-configuration.md#autonomous-population) ·
  [First claims](protocol/sovereignty.md#autonomous-first-claims) · [Population inspector](capsuleer-population.md#inspecting-bots-and-organizations)

The linked protocol records contain scope, source revisions and validation for individual milestones.

## Contents

- [Accounts, profiles and skills](#accounts-profiles-and-skills)
- [Chat, mail and social features](#chat-mail-and-social-features)
- [Cooperative fleets](#cooperative-fleets)
- [Player corporations](#player-corporations)
- [Travel and navigation](#travel-and-navigation)
- [Player Upwell structures](#player-upwell-structures)
- [Inventory, ships and holds](#inventory-ships-and-holds)
- [Personal contracts](#personal-contracts)
- [Mining, industry and economy](#mining-industry-and-economy)
- [Combat and NPC populations](#combat-and-npc-populations)
- [Fitting, weapons and modules](#fitting-weapons-and-modules)
- [Protocol coverage](#protocol-coverage)

## Accounts, profiles and skills

The separate [Avalonia desktop launcher](https://github.com/CJ-SPT/Eden-Launcher) supports account registration and local/remote launching through API v1. New standard and developer accounts receive Omega clone status by default. See [launcher hosting](launcher.md) for server configuration, existing account passwords and developer invitations. The default launcher API address is `http://127.0.0.1:26002`.

The native localization QA settings update now accepts its boolean flag before and after character selection and keeps it for the connection. See the [localization compatibility notes](protocol/compatibility.md#localization-qa-wrap-compatibility-november-5-2026).

Insider's QA menu can read and toggle the connection's Asset Holding QA settings. Its jump-clone command clears the selected
pilot's saved cooldown. Entitlement failure simulation, HyperNet seeding and test-token generation require separate service
work. See the [Insider QA guide](development-client.md#insider-qa-tab).

Character-sheet Combat Log now shows saved NPC kills and player ship losses, with native report details and historical records recovered from existing loss/bounty data. Catalog-driven NPC faction losses now persist their standing values and dated history together with each kill. See the [interaction history notes](protocol/characters.md#character-interaction-history-november-5-2026).

Skill extraction now supports the native client request with atomic skill removal, extractor consumption and Large Skill Injector delivery. It enforces the allocated-SP minimum, docked capsule requirement, Alpha level floors, queued/trained prerequisites and clone/implant restrictions. Receipts, history, restart persistence, concurrent requests and connected-session notifications have automated coverage; native presentation remains unverified. See the [skill extraction notes](protocol/characters.md#persistent-skill-extraction).

Skill injector activation now credits the unallocated-SP pool through the native client. All **five published injector profiles** support their own diminishing/fixed rewards, Mini limits or daily Alpha cooldown, with station-hangar ownership checks. Large/small conversion preserves injector capacity, and native allowance reads now return a number. The historical Absolute augmentor supports three undiminished uses and its expiry/hangover rules. Previews, consumption, training progress, receipts and connected-session updates have automated coverage; native presentation remains unverified. See the [skill injector notes](protocol/characters.md#persistent-skill-injectors).

Cerebral accelerators now support native consumption, active-booster reads, timed attribute bonuses and expiry-aware training. All **186 published pure learning-booster profiles** from the pinned catalog use their own slots, durations, age limits and injection deadlines. Biology extends timed effects; clone jumps and capsule loss preserve these character-owned boosters. Training predicts expirations, including offline progress and multiple boosters. Native presentation remains unverified. Combat boosters remain pending. See the [cerebral accelerator notes](protocol/characters.md#persistent-cerebral-accelerators).

All **25 published learning implants** now support native installation, destructive unplugging and character-sheet reads. Attribute bonuses affect training while remap points stay separate. Implants persist with each physical clone body, follow jump-clone swaps, and are destroyed with the body. Inventory consumption, effective skill requirements, queue rebasing and connected-session updates have automated coverage; native presentation remains unverified. Hardwiring, set implants and combat boosters remain pending. See the [learning implant notes](protocol/characters.md#persistent-learning-implants).

Unallocated skill points now persist and can be applied through native individual-skill, selected-level and queue workflows. All **59** fixed SP reward types from the pinned client can be issued through the redeem queue and credited once to an owned pilot, without creating inventory. Previews preserve saved state; application retains elapsed training, enforces Alpha skill limits, and refreshes the queue and fitted-ship brain. Unallocated points count toward Alpha passive-training limits. Native rendering remains unverified. See the [skill point notes](protocol/characters.md#persistent-unallocated-skill-points).

The account redeem queue supports administrator-issued inventory, fixed SP, currency, and Expert System rewards.
Claiming an Expert System activates it directly on the recipient. Grants have optional expiry and survive restarts.
Physical rewards support fixed stations and home-station delivery. Grant references prevent duplicate issuance, queue
updates reach connected accounts, and the queue supports trash and reversal of intact redeemed stacks. Other activation
rewards and blueprint metadata remain pending. [Redemption administration](development-client.md#issuing-account-redeem-rewards) ·
[Direct Expert System redemption](protocol/characters.md#direct-expert-system-redemption)

Personal jump clones support installation, naming, destruction and activation at NPC stations, with saved bodies, fees and cooldowns. Installation and activation each cost **900,000 ISK**. Swaps within one station do not start or reset a cooldown. Infomorph skills and Alpha limits apply. The client can leave a docked ship in its hangar before jumping. Learning implants persist with each body. Rorqual and Titan vats can install and receive clones. Pochven NPC clone bays allow installation and local swaps at **+3.00 Triglavian Collective standing**. Upwell installation and native presentation remain open. See the [jump-clone notes](protocol/characters.md#persistent-personal-jump-clones), [Pochven rules](protocol/characters.md#pochven-npc-clone-bays) and [mobile jump clones](protocol/capital-logistics.md#mobile-jump-clones).

The agent directory includes all active pinned-catalog agents. Pilots can find agents, save contacts, and accept supported
combat, delivery, trade, mining, Data Center, and Paragon missions. All 100 career agents offer their empire's chain,
including combat, scanning, hacking, and industry lessons. R&D agents support passive research, paid datacore redemption,
and bonus assignments. Mission progress, direct mission penalties, and capped derived faction changes for ordinary
storylines persist through resident checkpoints.
The station Agents tab checks Event agents against the pilot's current mission and Eden's supported assignments.
[Nine epic arcs](missions.md#epic-arcs) offer branching progression, cross-agent handoffs and a 90-day replay interval.
[Supported missions](missions.md) · [Agent and research records](protocol/agents.md)

Home stations now persist independently of ship travel. Select the current NPC station or your school corporation’s headquarters for **100,000 ISK**. A docked capsule away from home can use **Self-Destruct** in the home-station panel to return there in a replacement capsule; other ships, wallet and skills stay in place. New pilots retain their starting station as home. Player-corporation remote offices remain pending. See the [home-station notes](protocol/characters.md#persistent-home-stations-and-docked-clone-transfer).

Task tracking now retains per-pilot client counters and catalog task completions across restarts. Supported server gameplay records its own progress, and the first-warp camera query reflects actual warp entry. Build 3662168 disables its legacy achievement UI; these routes do not enable that interface. See the [task tracking notes](protocol/characters.md#persistent-task-tracking).

Character settings support the stock client's read, save and delete calls. Each pilot's opaque text or binary values survive reconnects and server restarts, with the client's 100 KiB value limit. Native settings presentation remains unverified; see the [placeholder route implementation record](protocol/compatibility.md#placeholder-route-implementation-november-4-2026).

Each account has three character slots. Choose **Add Character** to create a pilot in the stock client. New pilots start docked at their school's AIR station with their race's corvette and starting skills. The location panel shows the system, security status, constellation, region, and station.

The Character Sheet opens for Eden Pilot and shows the Jita home station. Each local pilot has a saved account profile, pilot identity, and the starting skills for their race from the pinned catalog. Character selection and the Skill Catalogue show 384,047 SP for fresh seeded Caldari pilots. Buy a physical skillbook, right-click **Inject Skill**, then train it through the existing queue. Injection consumes one book and starts at zero SP; training checks prerequisites and the local Alpha limits. Queue progress, pause and resume survive restarts. See the [character-sheet service roadmap](protocol/characters.md#character-sheet-service-roadmap).

Personal Skill Plans support creating, editing, and deleting plans with ordered skill levels and milestones. Plans and
tracking survive restarts. Each pilot can track a personal or certified plan.
[Persistent personal skill plans](protocol/characters.md#persistent-personal-skill-plans)

Expert Systems can grant temporary levels for operating ships and modules. Rentals and top-ups survive restarts. The
Skill Catalogue shows virtual levels alongside permanent skill training. [Resident Expert Systems](protocol/characters.md#resident-expert-systems)

The admin site's **Progression → Skill training speed** sets the server rate for newly scheduled training after restart. The client uses that rate for new estimates and the saved timeline for scheduled levels. Pilots can pause and resume to apply the active rate to an existing queue while keeping earned progress. See the [skill training speed notes](protocol/characters.md#world-skill-training-speed-november-21-2026).

The Character Sheet's **Attributes** tab now has saved neural attributes and remapping: 99 base points, 17–27 per attribute, one timed remap every 365 days, and two initial bonus remaps. Remapping preserves earned SP and recalculates the remaining training queue. The **Biography** tab supports Unicode text and formatting, saves through the client's autosave call, and retains each pilot's biography across restarts. These additions have automated coverage; native presentation and editing remain unverified. Implants, boosters and clone management remain separate work. See [attributes and biography](protocol/characters.md#character-sheet-attributes-and-biography-november-4-2026).

Pilot **Show Info** has public-profile and corporation replies for another saved pilot, including a pilot on another account who is offline. The response includes saved identity, birth date and Unicode biography. The header includes the explicit no-militia field required before tab construction; medal and rank reads reflect the current empty state, and employment history shows the saved starter corporation. Automated coverage checks native reply shapes, startup requests and saved-data reads; native presentation remains unverified. See [pilot Show Info startup](protocol/characters.md#pilot-show-info-header-and-tab-startup-follow-up-november-4-2026).

The **Notes** tab reads and saves private notes about local pilots and catalog NPCs. Notes preserve Unicode and formatting up to the native 5,000-character limit, remain separate for each viewing character, and survive server restarts. Clearing a note removes only that character's saved entry. The server upgrades existing saves to schema 64 on startup. Automated coverage checks privacy, persistence and the native calls; editor presentation and close/autosave timing remain unverified. See [private pilot notes](protocol/characters.md#private-pilot-show-info-notes-november-4-2026).

**Log Off** now resets the selected character while retaining the authenticated connection, allowing another owned pilot to be selected. It checkpoints the ship, clears outgoing module/target state and old bindings, and keeps skill training running offline. In-space ships retain timed presence and incoming player tackle; see [persistent aggression](protocol/combat.md#enforced-safety-and-persistent-aggression-november-6-2026). Automated session coverage passes; the native return to character selection remains unverified. See [character logoff](protocol/startup.md#character-logoff-to-selection-november-4-2026).

## Chat, mail and social features

Native chat now supports multi-pilot Local, corporation and alliance chat, and private conversations over TLS. Local membership follows the selected pilot’s system and cleans up on disconnect/logoff; delayed Local hides silent peers. Chat completes the native availability handshake before joining channels. Initial joins include complete native rosters, and a bounded startup wait preserves the first message racing its system-channel join. The companion launcher provisions certificate trust only in prepared client copies. Server-side archives and persistent custom channel settings remain deferred. See the [chat notes](protocol/social.md#native-chat-and-multi-pilot-local-november-5-2026).

Mailing lists now support native creation/joining, membership and role management, access rules, welcome messages and subscriber broadcasts. Delivery snapshots keep subscriber identities private, preserve each mailbox's state and retain list names in historical mail. Native presentation remains unverified. Corporation/alliance mail is implemented; mailing-list alliance access rules and paid CSPA remain pending. See the [mailing-list notes](protocol/social.md#persistent-mailing-lists).

Mail-arrival events now match the stock client's eight-argument callback. An extra label-mask argument previously caused the recipient callback to be rejected before its native unread/popup handling. See the [arrival-event correction](protocol/social.md#mail-arrival-notification-arity); live popup/blink confirmation remains pending.

New personal mail now accepts the stock compose window's `False` defaults for unused reply/forward IDs. This fixes the reported **Expected a mail identifier or None** rejection when sending to another pilot. See the [compose-default correction](protocol/social.md#mail-compose-optional-source-defaults); live confirmation of the fix remains pending.

Personal EVE Mail now supports local-pilot sending, inbox/sent state, replies/forwards, compressed bodies, read/unread actions, trash/restoration/deletion and private labels. Mail and change notifications persist across restarts, with recipient ownership, blocking and rate limits enforced atomically. Native presentation remains unverified. Corporation/alliance broadcasts now snapshot their member recipients; paid CSPA remains pending. Mailing lists are implemented above. See the [personal mail notes](protocol/social.md#persistent-personal-eve-mail).

The notification inbox now saves contact-add and contact-edit notifications, including the contact form's **Send Notification** message. Recipients can fetch history/unread groups, mark items read and delete selected items, groups or all history. Connected pilots receive native notifications; offline recipients retain them across restarts. Blocked messages fail together with the contact edit. Other gameplay notification families and paid CSPA communication remain pending; native inbox presentation is unverified. See the [notification inbox notes](protocol/social.md#persistent-notification-inbox-and-contact-messages).

Personal calendar events now support saved invitations, details, replies, edits, rescheduling and cancellation. Creators can invite up to **50 local Eden pilots**; connected invitees receive ordered calendar updates, and offline invitees see their events after reconnecting. Removing an invitee revokes access. Alliance calendars are implemented with current-member access and executor administration. Corporation members can see and respond to corporation events. Communications Officers, directors and CEOs can schedule, reschedule and cancel them. See [corporation calendar events](protocol/social.md#corporation-calendar-events). Official events and paid CSPA invitations remain pending. Native calendar presentation is unverified; see the [calendar notes](protocol/social.md#persistent-personal-calendar).

Personal contacts support add/edit/delete, standings, blocking and labels for local Eden pilots, including offline pilots. Values survive restarts. Watchlists show online state and receive login/logoff updates without docking flicker. NPC agents can be saved through their native address-book action. Optional add/edit messages reach the recipient's saved notification inbox. Corporation and alliance shared contact books are implemented. Personal standings and labels also accept local player corporation and alliance owners; watchlists, blocking and notification messages remain character-only. Native contact-window presentation is unverified; see the [personal contact implementation notes](protocol/social.md#personal-contacts-and-online-status).

Station Guests lists all connected pilots docked at that station, with live arrival/departure updates for login, docking, undocking, logoff and disconnect. Offline pilots and pilots elsewhere are excluded. Native presentation of this fix remains unverified; see the [station guest protocol notes](protocol/social.md#station-guests-november-4-2026).

## Cooperative fleets

Pilots can create fleets, invite online pilots, manage command roles, broadcast, chat, and warp members to supported system
objects, bookmarks and same-system pilots. Members can opt out of fleet warp. Fleets support eligible mission reward and bounty
sharing, on-grid loot access, watchlist damage and Command Bursts.
[Cooperative fleet records](protocol/fleets.md)

Fleet Finder supports corporation and public adverts, with standings filters and open or approval-based applications.
Alliance and militia adverts, reconnect grace, saved setups and geometric formations remain open. Fleets and their adverts
last for the running server session. [Fleet Finder](protocol/fleets.md#fleet-finder-advert-creation-november-15-2026)

## Player corporations

Player corporations support persistent membership, delegated roles and titles, wallets, shared offices and asset reports,
recruitment, shares, dividends, voting and CEO succession. Alliance members can make qualifying EverMark donations to the
executor, and the executor can transfer EverMarks to member corporations.
[Corporation features](corporations.md) · [Alliance EverMark eligibility](protocol/alliances.md#alliance-membership-age-and-evermark-donations)

## Travel and navigation

Focused Heavy Interdiction Cruiser scripts prevent combat capitals and Capital Industrial Ships from jumping through
NPC stargates while the effect is active. Freighters, Jump Freighters and subcapitals can still jump under focused tackle.
[Focused interdiction at NPC stargates](protocol/navigation.md#focused-interdiction-at-npc-stargates)

The star map's **Personal → Systems Visited** filter shows each pilot's visit counts and latest timestamps. History starts
when the pilot first selects a character after upgrading and persists across world restarts.
[Personal map visit history](protocol/navigation.md#personal-map-visit-history)

Developer accounts can use **GM / WM Extras → TR me here!** on known solar systems, NPC stations and standard static celestials. Travel preserves the active hull, fittings, cargo, wallet and medical home. Cross-system space destinations use a three-tick native jump transition to hand off the camera and input controls; a docked departure first undocks and loads its source ballpark. Same-system moves keep the current flight connection and stream nearby contacts as the ship relocates. Station destinations are immediate. Native camera/menu recovery remains unverified. See the [translocation notes](protocol/navigation.md#developer-translocation) and [developer travel guide](development-client.md#developer-travel).

Map startup answers the native jump-bridge access, faction ownership and home-station lookups. The home marker uses the
saved medical home, and the bridge reply preserves the client's three-part tuple. The map and Alliance Systems window
read the world's sovereignty claims. [Map startup](protocol/navigation.md#native-map-startup) ·
[Home-station marker](protocol/navigation.md#map-home-station-row) · [Alliance Systems window](protocol/sovereignty.md#alliance-systems-window)

Undocking now uses each NPC station model's catalogued exit lane and launches with the pinned client's initial velocity. Static collisions retain thrust and tangential movement instead of cancelling the course. All 44 NPC station models have automated coverage, including Amsen VI - AIR Laboratories. Launches remain outside Eden's spherical station collision boundary, so exact placement at the visible hangar mouth remains approximate. See the [undocking correction](protocol/navigation.md#station-exit-lanes-and-collision-response).

Personal Locations now saves private folders, subfolders and bookmarks across reconnects and restarts. Save stations or map destinations, or save your authoritative position in space and warp back within that solar system. Edit, move, copy, delete, activate/forget folders, and use automatic expiry. Personal warps accept the native `fleet=False` flag, fixing **Unexpected bookmark warp arguments**; see the [compatibility fix](protocol/navigation.md#bookmark-warp-personal-fleet-flag). Shared access-group folders and scan-result bookmarks remain pending. Native bookmark-window presentation remains unverified; see the [personal bookmark notes](protocol/navigation.md#personal-bookmarks-and-saved-location-warp).

The station directory returns every catalog station for remote station lookup and map consumers, with catalog names, locations, owners and service masks. The pinned client assigns mask bits by sorted service IDs. Native map/window presentation remains unverified; see the [station directory notes](protocol/navigation.md#catalog-station-directory).

On the local server at `127.0.0.1:26000`, **Eden Pilot** can undock the Ibis from Jita IV – Moon 4, fly on a heading, stop, and dock at the Caldari Navy Assembly Plant. **Eden Wing** has a separate account and Ibis at the same station. Both pilots can warp to catalog objects within Jita, see each other leave and arrive in Overview, and coast to a stop under hull inertia. Planets and large moons have repeatable warp-in points. Ships align on their actual velocity, coast out of warp, and stop at solid catalog objects during sublight flight. Jita contains 18 stations, seven stargates, eight planets, 33 moons, and one star. Pilots can jump through paired stargates. The Windows client plays the gate transition and loads the destination system. A restart restores each ship in its hangar or at its last saved system and position in space.

Player and NPC ships share one authoritative movement step per system tick. Ship bumping follows the pinned client's legacy Destiny response, including stationary ships being pushed and unequal hull masses. Owners and observers share tick-stamped courses and native collision prediction. Stopped ships remain collision participants. Movement and collision results are saved together. See the [ship collision investigation and acceptance](protocol/navigation.md#shared-ship-motion-and-collision-correction).

Use **Orbit** or **Keep at Range** on a nearby ship, NPC, asteroid or wreck to control positioning. Orbits account for the two objects' radii and the ship's speed, mass and agility; tight or boosted orbits can settle wider than the requested range. Both controls track moving targets. Stop, a new heading, warp and docking cancel the course. When the target leaves, an orbiting ship continues on its current heading while a Keep at Range follower coasts to a stop. Restart retains position and returns the ship to rest.

## Player Upwell structures

Ordinary targeted warp disruption prevents Upwell docking while its active strength exceeds the ship's warp core strength.
Pilots can retry when the effect expires or their stabilizer ties the incoming strength.
[Warp disruption and Upwell docking](protocol/structures.md#warp-disruption-and-upwell-docking)

Pilots within range of a dockable Upwell structure can move eligible ship cargo into their personal Deliveries hangar
from space. They can see the deposit in Personal Assets and move it into Item Hangar after docking.
[Cargo Deposit at dockable Upwell structures](protocol/structures.md#cargo-deposit-at-dockable-upwell-structures)

Pilots can supply fuel blocks from space to an anchored Pharolux, Ansiblex or Tenebrex through **Cargo Deposit**. The
structure's corporation receives the fuel for its built-in service.
[Cargo Deposit at FLEX navigation structures](protocol/structures.md#cargo-deposit-at-flex-navigation-structures)

While docked at an Upwell structure, pilots can send eligible personal Item Hangar stacks to another pilot's Deliveries
hangar. The recipient's items and inbox notification persist across restarts.
[Personal Deliveries at Upwell structures](protocol/structures.md#personal-deliveries-at-upwell-structures)

Gunners can fit Standup defenses, load ammunition and scripts, lock from the structure's own sensors, fire defensive
missiles, neutralize capacitor, slow ships and disrupt warp. Loading the Standup scrambling script also blocks
microwarpdrives and micro jump drives. Standup fighters can launch and recall, including after the gunner
releases control. The structure audit tracks the remaining defensive effects, service-specific access and fees,
player markets, clone bays, corporation offices, and specialized hull behavior.
[Structure gameplay and gap audit](protocol/structures.md#structures-gap-audit)

High GMs can force all native Upwell operating states, set operating and deployment timers, start or cancel unanchoring,
and change an eligible structure's abandonment deadline. [Structure GM commands](protocol/structures.md#structure-gm-commands)

## Inventory, ships and holds

Inventory displays the saved wear of personally owned frequency and mining crystals through the native crystal damage query.
[Crystal damage in inventory](protocol/inventory.md#crystal-damage-in-inventory)

Pilots docked in a cored Upwell structure with repair access can use the native repair shop for free ship and module repairs.
[Upwell repair shop](protocol/structures.md#native-upwell-repair-shop)

The native **Personal Assets** window now receives station counts, solar-system/type metadata and remote item lists. Its location filters, sorting and text/advanced search run in the pinned client over server-provided personal station assets. Search includes reachable cargo-container contents with their original locations and flags, while ship fittings/cargo, foreign ownership, installed blueprints, escrow and assets in space remain excluded. The active docked ship is included. Reads work from space and reflect committed ownership changes; the native client retains its existing five-minute caches. **Asset Safety** now supports resident personal/corporate wraps, timed delivery and paid recovery; wormhole/Pochven collection ejects public loot. See [asset safety and public structure loot](protocol/structures.md#asset-safety-and-public-structure-loot). Asset-worth reporting remains separate work. See [personal asset protocol support](protocol/inventory.md#personal-assets-window-november-8-2026); native presentation remains unverified.

Inventory stack division now supports Shift-drag within the same hangar or active ship hold, plus the native Divide Stack menu while docked. A 50-unit injector stack can become separate 49- and one-unit stacks without changing location or consuming extra capacity. Splits preserve metadata, update the native inventory cache and survive restarts. See the [stack division correction](protocol/inventory.md#native-inventory-stack-division); native presentation remains unverified.

Ship dirt now uses a saved timestamp for each assembled hull, and the native **Clean Ship** action resets it. Aging survives repeated reads, restarts and ownership transfers. Hangar reads and space metadata share the same value, with cleaning updates for owners and observers. Hulls without previous dirt history begin tracking on their first authorized read. Access follows current ownership and inventory reachability; client rendering remains unverified. See the [ship dirt notes](protocol/inventory.md#persistent-ship-dirt-and-native-cleaning).

Pilots can activate eligible permanent and timed first-party **SKIN licenses** from inventory. Activated licenses
appear in their collection and persist across restarts. [First-party ship SKIN licenses](protocol/inventory.md#first-party-ship-skin-licenses)

Pilots can apply a licensed first-party SKIN in fitting and clear it to return to the default appearance. The selected
SKIN persists per hull for its owner and is visible to nearby ships in space.
[First-party ship SKIN application](protocol/inventory.md#first-party-ship-skin-application)

Newly created pilots start with an online Civilian Miner and Civilian Gatling Railgun in their corvette's two high slots when the starting-equipment setting is enabled. [Initial corvette fitting](protocol/characters.md#initial-corvette-fitting)

Drag equipment into the active ship's cargo and back while docked. Owned item stacks and cargo locations survive restarts; the server checks ownership, quantity and the hull's cargo capacity. A stack too large for the hold opens the client's quantity picker so you can load only what fits. Open Fitting with the station panel's circled ship button to fit mining lasers or the civilian railgun, take them offline or online, and unfit them. Drag an already fitted module to another compatible slot to move it, or onto a fitted module in the same rack to exchange their slots. Loaded rounds and paid cycle/reload deadlines stay with their module. Fitted modules and online state survive restarts. The server checks catalog rack requirements, independent hardpoint counts, module limits, required skills, CPU and power grid. Bring mined ore back to a station and drag it into Item Hangar to unload.

Buy a **Venture** for **1 ISK** at the testing market. Assemble it in Ship Hangar, name it, and board it while docked. Each ship retains its own cargo, fitting and capacitor. The Venture has 50 m³ cargo and a separate 5,000 m³ mining hold; mined ore enters that hold and can be unloaded or reprocessed at a station. Unload ore to Item Hangar to use the native Sell This Item menu. Ore yield includes the hull's 100% role bonus and 5% per trained Mining Frigate level. Assembly now admits **235 published Tech I hulls**: all 99 small hulls, 101 cruisers, battlecruisers and battleships, and 35 industrial/utility hulls, including faction and special editions in specialist groups. Ship switching supports owned, assembled hulls in the current station. Repackaging, trading assembled ships and boarding in space remain future work.

The station's **Board my Corvette** button boards a free recovery ship for the pilot's race, fitted with an online Civilian Miner and Civilian Gatling Railgun. Eden reuses an undamaged corvette with that equipment at the current station, or creates a new kit. The previous ship, cargo and wallet stay intact. This is Eden's recovery policy; the same civilian gun currently serves every race.

The Tech I hull goal has a reproducible **275-type** inventory, including faction and special editions. Its verified first cohort added **Merlin, Punisher, Incursus and Rifter**, with native NPC combat and catalog weapon, resistance and local repair bonuses. The verified small-hull expansion covers all 99 qualifying frigates, destroyers, shuttles and corvettes, including Chremoas, Imp, Raiju and Zephyr. Catalog bonuses feed the existing fitting and operation calculations; Dramiel, Chremoas and Mekubal also receive their 75% projectile reload reduction. The generated [per-hull capability record](tech-one-hulls.json) identifies fitting limits, holds and deferred roles such as drones, probes and hacking. See the [small-hull acceptance record](protocol/hulls.md#tech-i-hull-expansion-milestone-2) for all-hull automated checks, eleven additional native flight checks, corvette recovery and restart results. The combat expansion adds all 101 qualifying cruisers, battlecruisers and battleships, with medium/large weapon selectors, attack-battlecruiser fitting reductions and battleship tank roles. The [combat-hull acceptance record](protocol/hulls.md#tech-i-hull-expansion-milestone-3) records 1,477 automated tests and six representative native flight/combat checks. The [industrial and utility expansion](protocol/hulls.md#tech-i-hull-expansion-milestone-4) adds 35 hulls, personal specialized holds, assembled ship transport and ordinary strip miners. Its native checks cover hauling, rejected cargo, stored-ship recovery, barge mining, freighter fitting and a yacht flight. Assembly now admits 235 of the 275 catalog IDs. Capital admission, capsule exceptions and the final audit remain deferred; work stops after milestone 4.

## Personal contracts

Contract Search now uses `None` for the unused delivery station on exchanges and auctions, preventing a bogus structure-access lookup for station zero. Native `structureSettings.CharacterHasService` reads also return boolean docking access instead of **Invalid Packet Destination**. Existing offers need no migration. See the [endpoint-access correction](protocol/contracts.md#contract-search-endpoint-access).

Contract search replies now provide valid endpoint systems for item exchanges and auctions. The native search window resolves both system names before it renders results, so a zero endpoint could leave its status at **Search not started**. Existing saved offers need no migration or recreation. See the [search endpoint notes](protocol/contracts.md#contract-search-endpoint-metadata); native click-to-results acceptance remains unverified.

The personal contract wizard now supports the global inventory binding used by Pick Items, the station lookup used by public-contract confirmation, and the stock client's None values for optional submission confirmation, public assignees and unused destinations. These fix the reported unsupported `charMgr.MachoResolveObject`, `stationSvc.GetStation`, **Invalid contract flag** and **Expected a contract number** errors. See the [binding fix](protocol/contracts.md#contract-wizard-global-inventory-binding), [confirmation lookup](protocol/contracts.md#contract-confirmation-station-lookup), [submission default](protocol/contracts.md#contract-submission-confirmation-default) and [optional identifier](protocol/contracts.md#contract-submission-optional-identifiers) notes.

Bulk ship contracts now create one personal item-exchange offer per assembled ship, up to the pinned client's **30-ship** limit and the pilot's available skill slots. Each offer keeps its own price, fees, fitted contents and later settlement. The complete batch commits together; a rejected ship, insufficient funds or a failed write leaves all ships and funds unchanged. Native requests and progress notifications have automated coverage; client presentation remains unverified. See the [bulk ship contract notes](protocol/contracts.md#bulk-fitted-ship-contract-creation).

Personal auctions now support public offers and private assignments, held bids, automatic outbid refunds, repeated late-bid extensions and buyouts. Ordinary auction winners collect their goods after expiry; issuers collect proceeds separately. Buyouts deliver goods immediately. Bid history, claims, wallet entries and fitted hull contents survive restarts. Native calls have automated coverage; client presentation remains unverified. See the [auction notes](protocol/contracts.md#personal-auction-contracts).

Personal courier contracts now support public offers and private assignments between NPC stations. Acceptance creates a sealed package with its actual cargo volume and holds collateral; delivery settles the reward, collateral and public deposit together. Packages survive restarts and can survive ship destruction and wreck recovery. Failure pays the issuer, while breaking an intact package releases its goods to the hauler. Search, progress lists, wallet history and native calls have automated coverage; client presentation remains unverified. See the [courier notes](protocol/contracts.md#personal-courier-contracts).

Personal item-exchange contracts support public offers and assignments to local pilots, item/ISK escrow, requested packaged items, search, lists, acceptance, rejection, cancellation and expired-escrow collection. Settlement transfers assets and ISK together, retains fitted hulls and nested contents, and records wallet history. Native requests and notifications are covered by automated tests; client presentation remains unverified. Corporation/structure contracts and package subcontracting remain pending. See the [item-exchange notes](protocol/contracts.md#personal-item-exchange-contracts) and [asset preparation notes](protocol/contracts.md#personal-contract-asset-preparation).

## Mining, industry and economy

ISK and EverMarks redeem rewards now credit their wallets. ISK tokens previously fell through to inventory delivery. ISK gains have native journal entries and balance updates; personal LP reads return saved issuer balances, including Paragon EverMarks. Claims survive restarts, reject duplicates/overflow, and work before pilot selection or in space. Native wallet rendering remains unverified. Corporation donations are implemented; Paragon missions remain pending. See the [currency and loyalty-wallet notes](protocol/industry.md#currency-redemption-and-loyalty-wallets).

NPC loyalty point stores support native station access, offer browsing, personal purchases, CEO/Director corporate purchases from the selected ISK division, and personal CONCORD LP conversion at pinned catalog rates. Required items come from the purchasing pilot's station Item Hangar; LP/ISK, item consumption, delivery and purchase receipts commit together. A dated offline CCP offer snapshot is checked against the pinned catalog, exposing supported physical rewards. Cosmetic licenses, Analysis Kredit offers and event-specific access remain open. Supported missions award LP. Eligible NPC loyalty stores also offer [earned blueprint copies](protocol/industry.md#earned-lp-blueprint-copies). See [LP-store scope and validation](protocol/industry.md#loyalty-point-stores-november-8-2026).

Mine ordinary ore, ice, gas or Mercoxit with equipment admitted for the resource and hull. Modulated ore lasers use compatible
crystals with saved wear. At stations with reprocessing, preview and refine eligible materials using the station, recipe, and
trained skill modifiers. [Mining and refining records](protocol/industry.md)

Personal manufacturing at NPC stations uses supported published recipes, including Tech I/II/III items, capitals
where security allows, and packaged structures. Supercapital construction uses eligible player shipyards. Refineries
support [composite reaction jobs](protocol/industry.md#composite-reaction-jobs). Module activation, hull flight and supply of
materials or blueprints follow their own eligibility rules.
[Catalog-wide production](protocol/industry.md#catalog-wide-station-production-november-9-2026) ·
[Supercapital shipyards](protocol/capital-logistics.md#supercapital-docking-construction-and-fleet-bridging)

Originals support ME/TE research and copying at laboratory stations. Copies inherit research and retain limited manufacturing runs; invention consumes copy runs, datacores and optional generic decryptors. Ancient relic stacks now support their catalog Tech III invention choices and consume one relic per attempt. Outcomes are saved at installation, and delivery/cancellation cannot reroll them. Separate manufacturing/laboratory slots, fees, ownership checks, installation reservations, restart persistence and exactly-once delivery remain authoritative. Missing price references reject priced jobs rather than silently waiving component costs. See [catalog-wide production](protocol/industry.md#catalog-wide-station-production-november-9-2026), its reproducible [recipe audit](production-catalog.json), and the earlier [science implementation](protocol/industry.md#science-jobs-november-8-2026).

Eden's default testing market sells all **19,727 published market types** in the pinned client catalog for **1 ISK per unit**. Each station with a market service is populated when a docked pilot accesses it, with **1,000,000 units per type** and replenishing supply. Existing and new pilots receive **1,000,000 ISK once**, recorded as a GM Cash Transfer in their wallet journal; restarting never repeats the grant. Ships, modules, ammunition, skillbooks, blueprint originals and other published market items can be purchased into Item Hangar. Purchasing an item does not implement its missing gameplay: the assembly, fitting, activation and industry limits described below still apply. NPC buyback orders for Veldspar, Scordite, Tritanium and Pyerite retain their finite demand, original prices and sales tax. The native Regional Market browser displays stock and history from actual regional trades. Personal immediate trades require docking at the selling station. Personal station-local buy/sell listings now reserve goods and fully funded ISK escrow, match partial fills across orders, charge broker/relist fees and sales tax, and support modification, cancellation and expiry while the owner is offline. Orders, payments, delivery and notifications survive save/reopen. Corporation trading, remote/ranged orders and minimum volumes above one remain separate work. New worlds can explicitly disable testing supply and grants; see [personal market orders](protocol/industry.md#persistent-personal-market-orders-november-6-2026).

Economy worlds supply catalog-offered original blueprints and skillbooks at their NPC corporation's stations, using the pinned base price. Institutional stock defaults to 1,000 replenishing units per order and is saved independently of development stock; change it in the [first-start configuration draft](world-configuration.md#first-start). Finished ships, modules, ammunition and minerals are not institutionally supplied. The first supported production fits are a three-gun Merlin, a two-laser Venture and a Badger with expanded cargo. Their recipes, suppliers and all seven mineral sources are recorded in the [starter production audit](starter-production.json); see [milestone evidence](protocol/industry.md#tech-i-starter-production-chain-november-6-2026).

Catalog-backed belts now renew at a saved daily UTC boundary (11:00 by default), including when no pilot visits the system. Startup catches up overdue belts once; reconnecting does not reset depletion. New economy worlds save configurable material price inputs and industry indices, charge atomic industry/reprocessing fees, and value the manufactured starter hulls for insurance from their material recipes. See [resource renewal and economic charges](protocol/industry.md#renewable-belts-and-economic-service-charges-november-6-2026).

Native **Stack All** now consolidates compatible personal stacks in Item Hangar or an active ship hold without changing total volume or ownership. Repackaging supports inactive, empty, unfitted, undamaged supported ships and offline modules in the current station hangar. Repackaged ships lose their insurance without a refund. Damaged ships, fitted ships, loaded modules, active ships, corporate assets and destructive confirmation forms remain unsupported. See [stacking and repackaging](protocol/inventory.md#production-stack-consolidation-and-repackaging-november-6-2026).

## Combat and NPC populations

- The administration killboard shows recorded ship and capsule destruction across the server, with independent NPC,
  player and bot filters for final blows and losses. [Server killboard](protocol/combat.md#server-killboard)
- Belt pirates use regional catalog pools with supported missiles, turrets, electronic warfare and self-repairs.
  Empire guards defend gates and stations, and civilian convoys and NPC mining fleets run local routes.
  [Populations and combat behavior](protocol/combat.md)
- Locks use scan resolution, target signature, range and the fitted target limit. Pilot attacks follow safety and aggression
  rules, with a CONCORD response to criminal activity in high-security space.
  [Safety and aggression](protocol/combat.md#enforced-safety-and-persistent-aggression-november-6-2026) ·
  [CONCORD](protocol/combat.md#concord-criminal-response--november-9-2026)
- Criminal player attacks award lasting kill rights. Owners can activate a right for free or list it for public or
  restricted access. Activation makes the target suspect for 15 minutes. [Character kill rights](protocol/combat.md#character-kill-rights)
- NPC bounties and mining and ratting SP settle on a configurable per-pilot tick. Fleetmates can share eligible bounties and loot.
  Ordinary NPC wrecks belong to the pilot who dealt the most damage, and mission wrecks go to the mission owner.
  Eligible security gains, ship losses and wrecks persist. Pilot
  capsule loss destroys active-body implants and returns the pilot to their saved home station.
  [Bounty ticks](protocol/combat.md#bounty-and-activity-sp-ticks) · [Security gain](protocol/combat.md#persistent-security-status-from-pirate-kills) ·
  [Capsule loss](protocol/combat.md#capsule-destruction-and-home-station-respawn-november-7-2026) ·
  [NPC wreck ownership](protocol/inventory.md#npc-wreck-ownership-from-damage)
- Docked pilots can insure eligible ships and repair at NPC station facilities or the native Upwell repair shop where
  available. Eligible Upwell structures also provide tether repair and asset safety.
  [NPC station repair](protocol/combat.md#native-station-repair-november-2-2026) · [Upwell repair](protocol/structures.md#native-upwell-repair-shop)

## Fitting, weapons and modules

- **Fitting.** Swap and rearrange modules, group weapons, load charges and save personal, corporation or alliance fittings.
  Apply supported fits with Multifit. Administrators can scale CPU, power grid, calibration and drone bandwidth.
  Fitting simulation uses the selected pilot's ship and combat-drone skill bonuses.
  Drag loaded charges between compatible modules in the fitting window.
  In space, fitted modules can go offline without cost. Bringing them online away from a fitting service requires 95% capacitor.
  [Drag loaded charges](protocol/fitting.md#dragging-loaded-charges-between-fitting-slots) ·
  [Module online state](protocol/fitting.md#module-online-state-in-space) ·
  [Fitting simulation](protocol/fitting.md#trained-skills-in-fitting-simulation) ·
  [Fitting records](protocol/fitting.md) · [World settings](world-configuration.md#fitting)
- **Weapons and defense.** Use supported hybrid, projectile and energy turrets, missiles, drones, smartbombs, and local or remote
  repairs. Scripts, ammunition, crystals, skills and hull bonuses affect their supported equipment. Native overload controls
  apply paid heat and module damage. Nanite paste repairs supported module damage.
  [Weapon and module records](protocol/fitting.md) ·
  [Overheating](protocol/fitting.md#complete-overheating-and-native-controls-november-8-2026)
- **Marauders.** Bastion Module I and its faction variants stop propulsion and apply the catalog's weapon, repair and resistance
  modifiers during their 60-second cycles. The weapons timer continues for 60 seconds after the cycle ends.
  [Marauder Bastion modules](protocol/fitting.md#marauder-bastion-modules)
- **Mobility and electronic warfare.** Use supported propulsion, cloaks, micro jumps, tackle, capacitor warfare and remote
  assistance. Interdictors, focused HIC scripts and nullifiers have their own fitting and activation rules.
  [Travel](protocol/navigation.md) · [Interdiction](protocol/interdiction.md) · [Area weapons](protocol/area-weapons.md)
- **Command Destroyers.** The subcapital Micro Jump Field Generator moves the source and up to 24 nearby eligible ships
  together. [Subcapital Micro Jump Field Generator](protocol/navigation.md#subcapital-micro-jump-field-generator)
- **Drones and information modules.** Supported hulls can control combat, mining and utility drones. Combat drones can
  assist and guard fleetmates. Scanners, probes, salvagers and tractors use their own eligibility and range rules.
  [Drones](protocol/drones.md) · [Information modules](protocol/information.md)
- **Capital and Upwell roles.** Supported capitals have ship-specific jump, fighter, fleet hangar, maintenance, refitting and clone
  services. Upwell gunners can lock targets and launch and recall Standup fighter squadrons.
  [Capital logistics](protocol/capital-logistics.md) · [Upwell structures](protocol/structures.md)

### Ship and module completion program

The [ship content inventory](ship-content-coverage.json) records the generated snapshot of hull assembly and fitting
admission, capability checks and deferred roles for build 3662168. Ship assembly, an implemented role and an obtainable
production chain are tracked separately. The [Tech I inventory](tech-one-hulls.json) records the earlier expansion cohorts.
See the [original coverage program](protocol/fitting.md#ship-content-coverage-milestone-1-november-7-2026) and the
[Tech II subcapital admission record](protocol/fitting.md#t2-subcapital-hull-admission-november-9-2026) for scope and evidence.

From the repository root, regenerate the reports with the installed catalog and absolute output paths.

```powershell
$catalog = Join-Path $PWD 'src/Eden.Server/data/catalog.sqlite'
$hulls = Join-Path $PWD 'docs/tech-one-hulls.json'
$coverage = Join-Path $PWD 'docs/ship-content-coverage.json'
dotnet run --no-build --artifacts-path artifacts/feature-tests --project src/Eden.Server -- --export-hull-coverage $catalog $hulls
dotnet run --no-build --artifacts-path artifacts/feature-tests --project src/Eden.Server -- --export-ship-content-coverage $catalog $coverage
```

## Protocol coverage

The [client RPC inventory](client-routes/README.md) lists missing handler candidates and compatibility routes from inert inspection of pinned build 3662168. It includes sortable route lists, client call sites and a repeatable audit tool; dynamic calls and partial feature support still need review.
