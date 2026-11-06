# Protocol findings and milestone records

[Documentation index](README.md)

Use the topic pages for native request/reply shapes, implementation findings, source revisions, automated validation and historical client observations. Records have moved by topic; their evidence and limitations are retained.

Historical native observations apply only to the recorded revision and actions. Agents perform automated testing only unless live testing is explicitly requested; see the [testing policy](../AGENTS.md#testing).

## Topics

| Topic | Coverage |
| --- | --- |
| [Startup and authentication](protocol/startup.md) | Handshake, encryption, launcher accounts, character selection and initial sessions. |
| [Catalog and provenance](protocol/catalog.md) | Pinned-build data, export inputs and catalog verification. |
| [Navigation and travel](protocol/navigation.md) | Maps, bookmarks, undocking, warp, gates, orbiting and ship collisions. |
| [Characters and skills](protocol/characters.md) | Profiles, settings, training, clones, implants, redemption and interaction history. |
| [Chat, mail and contacts](protocol/social.md) | Native chat, certificate trust, contacts, guests, calendars and notifications. |
| [Corporations](protocol/corporations.md) | Creation, membership, administration, roles, wallets, ownership and shares. |
| [Personal contracts](protocol/contracts.md) | Wizard compatibility, item exchange, courier contracts, auctions and bulk offers. |
| [Inventory and ship ownership](protocol/inventory.md) | Stacks, personal holds, assembled ships, insurance, dirt and recovery hulls. |
| [Mining, industry and markets](protocol/industry.md) | Asteroids, mining, reprocessing, manufacturing, testing markets and loyalty balances. |
| [Combat and NPC populations](protocol/combat.md) | Targeting, health, weapons, NPC populations, [mining fleet corrections](protocol/combat.md#mining-fleet-warp-response-and-name-corrections-november-6-2026), [jamming and bracket cleanup](protocol/combat.md#npc-jamming-scan-cancellation-and-bracket-cleanup-november-6-2026), losses, wrecks and repairs. |
| [Fitting and module families](protocol/fitting.md) | Catalog fitting rules, weapon tiers, ammunition, repairs, propulsion and tackle. |
| [Combat drones](protocol/drones.md) | Brutix and Brutix Navy Issue light/medium drone launch, control, NPC combat, recall and saved state. |
| [Tech I hull expansion](protocol/hulls.md) | Hull cohorts, admission limits, utility roles and historical client observations. |
| [Architecture and typed boundaries](protocol/architecture.md) | Named models, Gameplay boundaries, orchestration, persistence and architecture audits. |
| [Session performance and resident state](protocol/performance.md) | Diagnostics, replay benchmarks, resident caches, combat objects and checkpoints. |
| [Compatibility and route audits](protocol/compatibility.md) | RPC inventory, placeholder routes, localization settings and native rejection dialogs. |

## Record index

- [Bounded authentication retention, November 6, 2026](protocol/startup.md#bounded-authentication-retention-november-6-2026)

- [Wallet history filtering and indexes, November 6, 2026](protocol/industry.md#wallet-history-filtering-and-indexes-november-6-2026)

- [Password verification outside world ownership, November 6, 2026](protocol/startup.md#password-verification-outside-world-ownership-november-6-2026)

- [Connection admission and stalled transport recovery, November 6, 2026](protocol/startup.md#connection-admission-and-stalled-transport-recovery-november-6-2026)

- [Launch inventory and normal-undock synchronization, November 6, 2026](protocol/drones.md#launch-inventory-and-normal-undock-synchronization-november-6-2026)
- [Brutix Navy Issue launch compatibility, November 6, 2026](protocol/drones.md#brutix-navy-issue-launch-compatibility-november-6-2026)
- [Brutix light and medium combat drones, November 6, 2026](protocol/drones.md#brutix-light-and-medium-combat-drones-november-6-2026)

The section anchors below preserve links to the former single-file notes. Follow a record link to its full text. Add future findings to the owning topic page and link them here.

### Startup and authentication

- <a id="launcher-omega-account-default-november-4-2026"></a>[Launcher Omega account default (November 4, 2026)](protocol/startup.md#launcher-omega-account-default-november-4-2026)
- <a id="launcher-accounts-and-developer-roles"></a>[Launcher accounts and developer roles](protocol/startup.md#launcher-accounts-and-developer-roles)
- <a id="main-integration-and-test-performance-november-4-2026"></a>[Main integration and test performance (November 4, 2026)](protocol/startup.md#main-integration-and-test-performance-november-4-2026)
- <a id="six-character-launcher-passwords-november-4-2026"></a>[Six-character launcher passwords (November 4, 2026)](protocol/startup.md#six-character-launcher-passwords-november-4-2026)
- <a id="startup-exchange"></a>[Startup exchange](protocol/startup.md#startup-exchange)
- <a id="acceptance-record"></a>[Acceptance record](protocol/startup.md#acceptance-record)
- <a id="encrypted-local-authentication"></a>[Encrypted local authentication](protocol/startup.md#encrypted-local-authentication)
- <a id="character-selection"></a>[Character selection](protocol/startup.md#character-selection)
- <a id="live-verification"></a>[Live verification](protocol/startup.md#live-verification)
- <a id="docked-session-and-inventory"></a>[Docked session and inventory](protocol/startup.md#docked-session-and-inventory)
- <a id="static-data-provenance"></a>[Static data provenance](protocol/startup.md#static-data-provenance)
- <a id="docked-acceptance-record"></a>[Docked acceptance record](protocol/startup.md#docked-acceptance-record)
- <a id="first-flight"></a>[First flight](protocol/startup.md#first-flight)
- <a id="first-flight-acceptance-november-2-2026"></a>[First-flight acceptance, November 2, 2026](protocol/startup.md#first-flight-acceptance-november-2-2026)
- <a id="two-pilots-in-jita-november-2-2026"></a>[Two pilots in Jita, November 2, 2026](protocol/startup.md#two-pilots-in-jita-november-2-2026)
- <a id="character-logoff-to-selection-november-4-2026"></a>[Character logoff to selection, November 4, 2026](protocol/startup.md#character-logoff-to-selection-november-4-2026)
- <a id="client-requested-exit-logging-november-4-2026"></a>[Client-requested exit logging, November 4, 2026](protocol/startup.md#client-requested-exit-logging-november-4-2026)

### Catalog and provenance

- <a id="build-3662168-game-catalog"></a>[Build 3662168 game catalog](protocol/catalog.md#build-3662168-game-catalog)
- <a id="catalog-acceptance-november-2-2026"></a>[Catalog acceptance, November 2, 2026](protocol/catalog.md#catalog-acceptance-november-2-2026)

### Navigation and travel

- <a id="npc-visibility-after-same-system-gm-translocation"></a>[NPC visibility after same-system GM translocation](protocol/navigation.md#npc-visibility-after-same-system-gm-translocation)
- <a id="npc-spawn-and-translocation-follow-up"></a>[NPC spawn and translocation follow-up](protocol/navigation.md#npc-spawn-and-translocation-follow-up)
- <a id="developer-translocation"></a>[Developer translocation](protocol/navigation.md#developer-translocation)
- <a id="camera-and-input-handoff"></a>[Camera and input handoff](protocol/navigation.md#camera-and-input-handoff)
- <a id="map-home-station-row"></a>[Map home station row](protocol/navigation.md#map-home-station-row)
- <a id="native-map-startup"></a>[Native map startup](protocol/navigation.md#native-map-startup)
- <a id="catalog-station-directory"></a>[Catalog station directory](protocol/navigation.md#catalog-station-directory)
- <a id="station-exit-lanes-and-collision-response"></a>[Station exit lanes and collision response](protocol/navigation.md#station-exit-lanes-and-collision-response)
- <a id="personal-bookmarks-and-saved-location-warp"></a>[Personal bookmarks and saved-location warp](protocol/navigation.md#personal-bookmarks-and-saved-location-warp)
- <a id="bookmark-warp-personal-fleet-flag"></a>[Bookmark warp personal fleet flag](protocol/navigation.md#bookmark-warp-personal-fleet-flag)
- <a id="within-system-warp-and-braking-november-2-2026"></a>[Within-system warp and braking, November 2, 2026](protocol/navigation.md#within-system-warp-and-braking-november-2-2026)
- <a id="shared-flight-ticks-and-celestial-warp-ins-november-2-2026"></a>[Shared flight ticks and celestial warp-ins, November 2, 2026](protocol/navigation.md#shared-flight-ticks-and-celestial-warp-ins-november-2-2026)
- <a id="stargate-travel-november-2-2026"></a>[Stargate travel, November 2, 2026](protocol/navigation.md#stargate-travel-november-2-2026)
- <a id="native-player-orbit-and-moving-navigation-targets"></a>[Native player Orbit and moving navigation targets](protocol/navigation.md#native-player-orbit-and-moving-navigation-targets)
- <a id="shared-ship-motion-and-collision-correction"></a>[Shared ship motion and collision correction](protocol/navigation.md#shared-ship-motion-and-collision-correction)

### Characters and skills

- <a id="character-interaction-history-november-5-2026"></a>[Character interaction history (November 5, 2026)](protocol/characters.md#character-interaction-history-november-5-2026)
- <a id="private-pilot-show-info-notes-november-4-2026"></a>[Private pilot Show Info notes (November 4, 2026)](protocol/characters.md#private-pilot-show-info-notes-november-4-2026)
- <a id="pilot-show-info-header-and-tab-startup-follow-up-november-4-2026"></a>[Pilot Show Info header and tab startup follow-up (November 4, 2026)](protocol/characters.md#pilot-show-info-header-and-tab-startup-follow-up-november-4-2026)
- <a id="other-pilot-show-info-november-4-2026"></a>[Other pilot Show Info (November 4, 2026)](protocol/characters.md#other-pilot-show-info-november-4-2026)
- <a id="character-settings"></a>[Character settings](protocol/characters.md#character-settings)
- <a id="persistent-task-tracking"></a>[Persistent task tracking](protocol/characters.md#persistent-task-tracking)
- <a id="persistent-home-stations-and-docked-clone-transfer"></a>[Persistent home stations and docked clone transfer](protocol/characters.md#persistent-home-stations-and-docked-clone-transfer)
- <a id="catalog-agent-directory-and-saved-agents"></a>[Catalog agent directory and saved agents](protocol/characters.md#catalog-agent-directory-and-saved-agents)
- <a id="persistent-personal-jump-clones"></a>[Persistent personal jump clones](protocol/characters.md#persistent-personal-jump-clones)
- <a id="persistent-account-redemption"></a>[Persistent account redemption](protocol/characters.md#persistent-account-redemption)
- <a id="persistent-unallocated-skill-points"></a>[Persistent unallocated skill points](protocol/characters.md#persistent-unallocated-skill-points)
- <a id="persistent-learning-implants"></a>[Persistent learning implants](protocol/characters.md#persistent-learning-implants)
- <a id="persistent-cerebral-accelerators"></a>[Persistent cerebral accelerators](protocol/characters.md#persistent-cerebral-accelerators)
- <a id="persistent-skill-extraction"></a>[Persistent skill extraction](protocol/characters.md#persistent-skill-extraction)
- <a id="persistent-skill-injectors"></a>[Persistent skill injectors](protocol/characters.md#persistent-skill-injectors)
- <a id="account-profiles-and-trained-skills-november-2-2026"></a>[Account profiles and trained skills, November 2, 2026](protocol/characters.md#account-profiles-and-trained-skills-november-2-2026)
- <a id="character-sheet-service-roadmap"></a>[Character-sheet service roadmap](protocol/characters.md#character-sheet-service-roadmap)
- <a id="character-sheet-attributes-and-biography-november-4-2026"></a>[Character-sheet attributes and biography, November 4, 2026](protocol/characters.md#character-sheet-attributes-and-biography-november-4-2026)
- <a id="skill-training-queue-november-2-2026"></a>[Skill training queue, November 2, 2026](protocol/characters.md#skill-training-queue-november-2-2026)
- <a id="local-skill-plan-gateway-november-2-2026"></a>[Local skill-plan gateway, November 2, 2026](protocol/characters.md#local-skill-plan-gateway-november-2-2026)
- <a id="character-creation-and-first-spawn-november-2-2026"></a>[Character creation and first spawn, November 2, 2026](protocol/characters.md#character-creation-and-first-spawn-november-2-2026)

### Chat, mail and contacts

- <a id="translocation-destination-local-handoff-november-6-2026"></a>[Translocation destination Local handoff (November 6, 2026)](protocol/social.md#translocation-destination-local-handoff-november-6-2026)
- <a id="translocation-chat-continuity-november-6-2026"></a>[Translocation chat continuity (November 6, 2026)](protocol/social.md#translocation-chat-continuity-november-6-2026)
- <a id="native-chat-ready-handshake-correction-november-5-2026"></a>[Native chat ready-handshake correction (November 5, 2026)](protocol/social.md#native-chat-ready-handshake-correction-november-5-2026)
- <a id="chat-startup-first-send-and-initial-roster-correction-november-5-2026"></a>[Chat startup, first send and initial roster correction (November 5, 2026)](protocol/social.md#chat-startup-first-send-and-initial-roster-correction-november-5-2026)
- <a id="chat-certificate-manifest-correction-november-5-2026"></a>[Chat certificate manifest correction (November 5, 2026)](protocol/social.md#chat-certificate-manifest-correction-november-5-2026)
- <a id="native-chat-and-multi-pilot-local-november-5-2026"></a>[Native chat and multi-pilot Local (November 5, 2026)](protocol/social.md#native-chat-and-multi-pilot-local-november-5-2026)
- <a id="personal-contacts-and-online-status"></a>[Personal contacts and online status](protocol/social.md#personal-contacts-and-online-status)
- <a id="persistent-personal-calendar"></a>[Persistent personal calendar](protocol/social.md#persistent-personal-calendar)
- <a id="persistent-notification-inbox-and-contact-messages"></a>[Persistent notification inbox and contact messages](protocol/social.md#persistent-notification-inbox-and-contact-messages)
- <a id="persistent-mailing-lists"></a>[Persistent mailing lists](protocol/social.md#persistent-mailing-lists)
- <a id="mail-arrival-notification-arity"></a>[Mail arrival notification arity](protocol/social.md#mail-arrival-notification-arity)
- <a id="mail-compose-optional-source-defaults"></a>[Mail compose optional source defaults](protocol/social.md#mail-compose-optional-source-defaults)
- <a id="persistent-personal-eve-mail"></a>[Persistent personal EVE Mail](protocol/social.md#persistent-personal-eve-mail)
- <a id="station-guests-november-4-2026"></a>[Station guests, November 4, 2026](protocol/social.md#station-guests-november-4-2026)
- <a id="native-request-metadata-correction-november-4-2026"></a>[Native request metadata correction, November 4, 2026](protocol/social.md#native-request-metadata-correction-november-4-2026)

### Corporations

- <a id="corporation-share-holdings-and-native-wallet-panels"></a>[Corporation share holdings and native wallet panels](protocol/corporations.md#corporation-share-holdings-and-native-wallet-panels)
- <a id="corporation-bills-and-market-transactions-panel-calls"></a>[Corporation Bills and Market Transactions panel calls](protocol/corporations.md#corporation-bills-and-market-transactions-panel-calls)
- <a id="corporation-isk-header-permission-correction"></a>[Corporation ISK header permission correction](protocol/corporations.md#corporation-isk-header-permission-correction)
- <a id="corporation-inventory-ownership-storage"></a>[Corporation inventory ownership storage](protocol/corporations.md#corporation-inventory-ownership-storage)
- <a id="corporate-lp-and-evermark-wallets"></a>[Corporate LP and EverMark wallets](protocol/corporations.md#corporate-lp-and-evermark-wallets)
- <a id="shared-corporation-isk-wallets-and-bounty-taxation"></a>[Shared corporation ISK wallets and bounty taxation](protocol/corporations.md#shared-corporation-isk-wallets-and-bounty-taxation)
- <a id="delegated-corporation-roles-and-titles"></a>[Delegated corporation roles and titles](protocol/corporations.md#delegated-corporation-roles-and-titles)
- <a id="persistent-corporation-administration"></a>[Persistent corporation administration](protocol/corporations.md#persistent-corporation-administration)
- <a id="player-corporation-character-selection-logo-correction"></a>[Player corporation character-selection logo correction](protocol/corporations.md#player-corporation-character-selection-logo-correction)
- <a id="corporation-creation-ceo-refresh-correction"></a>[Corporation creation CEO refresh correction](protocol/corporations.md#corporation-creation-ceo-refresh-correction)
- <a id="corporation-details-faction-enlistment-service-correction"></a>[Corporation Details faction-enlistment service correction](protocol/corporations.md#corporation-details-faction-enlistment-service-correction)
- <a id="corporation-window-voting-capability-startup-correction"></a>[Corporation window voting-capability startup correction](protocol/corporations.md#corporation-window-voting-capability-startup-correction)
- <a id="player-corporation-applications-and-membership"></a>[Player corporation applications and membership](protocol/corporations.md#player-corporation-applications-and-membership)
- <a id="player-corporations-creation-foundation"></a>[Player corporations: creation foundation](protocol/corporations.md#player-corporations-creation-foundation)

### Personal contracts

- <a id="contract-search-endpoint-access"></a>[Contract search endpoint access](protocol/contracts.md#contract-search-endpoint-access)
- <a id="contract-search-endpoint-metadata"></a>[Contract search endpoint metadata](protocol/contracts.md#contract-search-endpoint-metadata)
- <a id="contract-submission-optional-identifiers"></a>[Contract submission optional identifiers](protocol/contracts.md#contract-submission-optional-identifiers)
- <a id="contract-submission-confirmation-default"></a>[Contract submission confirmation default](protocol/contracts.md#contract-submission-confirmation-default)
- <a id="contract-confirmation-station-lookup"></a>[Contract confirmation station lookup](protocol/contracts.md#contract-confirmation-station-lookup)
- <a id="contract-wizard-global-inventory-binding"></a>[Contract wizard global inventory binding](protocol/contracts.md#contract-wizard-global-inventory-binding)
- <a id="personal-contract-asset-preparation"></a>[Personal contract asset preparation](protocol/contracts.md#personal-contract-asset-preparation)
- <a id="personal-item-exchange-contracts"></a>[Personal item-exchange contracts](protocol/contracts.md#personal-item-exchange-contracts)
- <a id="personal-courier-contracts"></a>[Personal courier contracts](protocol/contracts.md#personal-courier-contracts)
- <a id="personal-auction-contracts"></a>[Personal auction contracts](protocol/contracts.md#personal-auction-contracts)
- <a id="bulk-fitted-ship-contract-creation"></a>[Bulk fitted-ship contract creation](protocol/contracts.md#bulk-fitted-ship-contract-creation)

### Inventory and ship ownership

- <a id="personal-ship-insurance"></a>[Personal ship insurance](protocol/inventory.md#personal-ship-insurance)
- <a id="native-inventory-stack-division"></a>[Native inventory stack division](protocol/inventory.md#native-inventory-stack-division)
- <a id="persistent-ship-dirt-and-native-cleaning"></a>[Persistent ship dirt and native cleaning](protocol/inventory.md#persistent-ship-dirt-and-native-cleaning)
- <a id="persistent-cargo-and-hangar-items-november-2-2026"></a>[Persistent cargo and hangar items, November 2, 2026](protocol/inventory.md#persistent-cargo-and-hangar-items-november-2-2026)
- <a id="partial-stack-capacity-recovery-november-2-2026"></a>[Partial-stack capacity recovery, November 2, 2026](protocol/inventory.md#partial-stack-capacity-recovery-november-2-2026)
- <a id="venture-ownership-boarding-and-mining-holds"></a>[Venture ownership, boarding and mining holds](protocol/inventory.md#venture-ownership-boarding-and-mining-holds)
- <a id="native-recovery-corvettes-november-2-2026"></a>[Native recovery corvettes, November 2, 2026](protocol/inventory.md#native-recovery-corvettes-november-2-2026)
- <a id="inventory-refresh-during-capsule-adoption-november-2-2026"></a>[Inventory refresh during capsule adoption, November 2, 2026](protocol/inventory.md#inventory-refresh-during-capsule-adoption-november-2-2026)

### Mining, industry and markets

- [Persistent personal market orders (November 6, 2026)](protocol/industry.md#persistent-personal-market-orders-november-6-2026)

- <a id="personal-market-corporation-wallet-checkbox-compatibility-november-6-2026"></a>[Personal market corporation-wallet checkbox compatibility (November 6, 2026)](protocol/industry.md#personal-market-corporation-wallet-checkbox-compatibility-november-6-2026)

- <a id="currency-redemption-and-loyalty-wallets"></a>[Currency redemption and loyalty wallets](protocol/industry.md#currency-redemption-and-loyalty-wallets)
- <a id="mining-laser-fitting-november-2-2026"></a>[Mining-laser fitting, November 2, 2026](protocol/industry.md#mining-laser-fitting-november-2-2026)
- <a id="persistent-asteroid-fields-november-2-2026"></a>[Persistent asteroid fields, November 2, 2026](protocol/industry.md#persistent-asteroid-fields-november-2-2026)
- <a id="civilian-mining-cycles-november-2-2026"></a>[Civilian mining cycles, November 2, 2026](protocol/industry.md#civilian-mining-cycles-november-2-2026)
- <a id="station-ore-reprocessing"></a>[Station ore reprocessing](protocol/industry.md#station-ore-reprocessing)
- <a id="immediate-station-market-and-personal-wallet"></a>[Immediate station market and personal wallet](protocol/industry.md#immediate-station-market-and-personal-wallet)
- <a id="mining-upgrades-and-regional-trade-history-november-2-2026"></a>[Mining upgrades and regional trade history, November 2, 2026](protocol/industry.md#mining-upgrades-and-regional-trade-history-november-2-2026)
- <a id="manufacturing-from-mined-minerals-november-2-2026"></a>[Manufacturing from mined minerals, November 2, 2026](protocol/industry.md#manufacturing-from-mined-minerals-november-2-2026)
- <a id="tech-i-module-manufacturing"></a>[Tech I module manufacturing](protocol/industry.md#tech-i-module-manufacturing)
- <a id="full-catalog-testing-market-world-version-28"></a>[Full catalog testing market (world version 28)](protocol/industry.md#full-catalog-testing-market-world-version-28)

### Combat and NPC populations

- <a id="high-security-npc-mining-operations-november-6-2026"></a>[High-security NPC mining operations (November 6, 2026)](protocol/combat.md#high-security-npc-mining-operations-november-6-2026)
- <a id="passive-high-security-station-convoys-november-6-2026"></a>[Passive high-security station convoys (November 6, 2026)](protocol/combat.md#passive-high-security-station-convoys-november-6-2026)
- <a id="empire-patrol-bracket-colors-november-6-2026"></a>[Empire patrol bracket colors (November 6, 2026)](protocol/combat.md#empire-patrol-bracket-colors-november-6-2026)
- <a id="catalog-driven-npc-electronic-warfare-november-5-2026"></a>[Catalog-driven NPC electronic warfare (November 5, 2026)](protocol/combat.md#catalog-driven-npc-electronic-warfare-november-5-2026)
- <a id="catalog-driven-npc-self-repair-november-5-2026"></a>[Catalog-driven NPC self-repair (November 5, 2026)](protocol/combat.md#catalog-driven-npc-self-repair-november-5-2026)
- <a id="development-npcs-excluded-from-regional-belts"></a>[Development NPCs excluded from regional belts](protocol/combat.md#development-npcs-excluded-from-regional-belts)
- <a id="local-empire-guard-populations"></a>[Local empire guard populations](protocol/combat.md#local-empire-guard-populations)
- <a id="regional-belt-pirate-populations"></a>[Regional belt pirate populations](protocol/combat.md#regional-belt-pirate-populations)
- <a id="npc-missile-combat"></a>[NPC missile combat](protocol/combat.md#npc-missile-combat)
- <a id="npc-population-foundation"></a>[NPC population foundation](protocol/combat.md#npc-population-foundation)
- <a id="persistent-security-status-from-pirate-kills"></a>[Persistent security status from pirate kills](protocol/combat.md#persistent-security-status-from-pirate-kills)
- <a id="native-target-locking-november-2-2026"></a>[Native target locking, November 2, 2026](protocol/combat.md#native-target-locking-november-2-2026)
- <a id="combat-calculation-groundwork-november-2-2026"></a>[Combat calculation groundwork, November 2, 2026](protocol/combat.md#combat-calculation-groundwork-november-2-2026)
- <a id="saved-ship-health-november-2-2026"></a>[Saved ship health, November 2, 2026](protocol/combat.md#saved-ship-health-november-2-2026)
- <a id="persistent-belt-npc-contacts-november-2-2026"></a>[Persistent belt NPC contacts, November 2, 2026](protocol/combat.md#persistent-belt-npc-contacts-november-2-2026)
- <a id="civilian-weapon-firing-npc-destruction-and-bounties-november-2-2026"></a>[Civilian weapon firing, NPC destruction and bounties, November 2, 2026](protocol/combat.md#civilian-weapon-firing-npc-destruction-and-bounties-november-2-2026)
- <a id="rookie-retaliation-and-capsule-escape-november-2-2026"></a>[Rookie retaliation and capsule escape, November 2, 2026](protocol/combat.md#rookie-retaliation-and-capsule-escape-november-2-2026)
- <a id="native-station-repair-november-2-2026"></a>[Native station repair, November 2, 2026](protocol/combat.md#native-station-repair-november-2-2026)
- <a id="rookie-wrecks-and-native-looting-november-2-2026"></a>[Rookie wrecks and native looting, November 2, 2026](protocol/combat.md#rookie-wrecks-and-native-looting-november-2-2026)
- <a id="player-wrecks-and-surviving-inventory-november-2-2026"></a>[Player wrecks and surviving inventory, November 2, 2026](protocol/combat.md#player-wrecks-and-surviving-inventory-november-2-2026)
- <a id="retaliatory-npc-pursuit-and-return-november-2-2026"></a>[Retaliatory NPC pursuit and return, November 2, 2026](protocol/combat.md#retaliatory-npc-pursuit-and-return-november-2-2026)
- <a id="automatic-belt-npc-aggression"></a>[Automatic belt NPC aggression](protocol/combat.md#automatic-belt-npc-aggression)
- <a id="npc-combat-orbiting"></a>[NPC combat orbiting](protocol/combat.md#npc-combat-orbiting)

### Fitting and module families

- <a id="personal-saved-fitting-plans-november-6-2026"></a>[Personal saved fitting plans (November 6, 2026)](protocol/fitting.md#personal-saved-fitting-plans-november-6-2026)
- <a id="persistent-weapon-grouping"></a>[Persistent weapon grouping](protocol/fitting.md#persistent-weapon-grouping)
- <a id="shield-boosters-and-physical-skillbooks-november-2-2026"></a>[Shield boosters and physical skillbooks, November 2, 2026](protocol/fitting.md#shield-boosters-and-physical-skillbooks-november-2-2026)
- <a id="fitting-defense-rate-selector-november-2-2026"></a>[Fitting defense-rate selector, November 2, 2026](protocol/fitting.md#fitting-defense-rate-selector-november-2-2026)
- <a id="small-hybrid-turrets-and-ammunition-november-2-2026"></a>[Small hybrid turrets and ammunition, November 2, 2026](protocol/fitting.md#small-hybrid-turrets-and-ammunition-november-2-2026)
- <a id="trained-weapon-tooltip-calculations-november-2-2026"></a>[Trained weapon tooltip calculations, November 2, 2026](protocol/fitting.md#trained-weapon-tooltip-calculations-november-2-2026)
- <a id="afterburner-propulsion-and-navigation-skills"></a>[Afterburner propulsion and Navigation skills](protocol/fitting.md#afterburner-propulsion-and-navigation-skills)
- <a id="rearranging-fitted-modules"></a>[Rearranging fitted modules](protocol/fitting.md#rearranging-fitted-modules)
- <a id="fitting-expansion-milestone-1-catalog-fitting-definitions"></a>[Fitting expansion milestone 1: catalog fitting definitions](protocol/fitting.md#fitting-expansion-milestone-1-catalog-fitting-definitions)
- <a id="fitting-expansion-milestone-2-hull-passives-and-shared-modifiers"></a>[Fitting expansion milestone 2: hull passives and shared modifiers](protocol/fitting.md#fitting-expansion-milestone-2-hull-passives-and-shared-modifiers)
- <a id="fitting-expansion-milestone-3-passive-equipment-bonuses"></a>[Fitting expansion milestone 3: passive equipment bonuses](protocol/fitting.md#fitting-expansion-milestone-3-passive-equipment-bonuses)
- <a id="repeating-module-hud-timer-repair"></a>[Repeating module HUD timer repair](protocol/fitting.md#repeating-module-hud-timer-repair)
- <a id="fitting-expansion-milestone-4-local-tech-i-armor-repairers"></a>[Fitting expansion milestone 4: local Tech I armor repairers](protocol/fitting.md#fitting-expansion-milestone-4-local-tech-i-armor-repairers)
- <a id="fitting-expansion-milestone-5-active-tech-i-hardeners"></a>[Fitting expansion milestone 5: active Tech I hardeners](protocol/fitting.md#fitting-expansion-milestone-5-active-tech-i-hardeners)
- <a id="fitting-expansion-milestone-6-tech-i-capacitor-boosters"></a>[Fitting expansion milestone 6: Tech I capacitor boosters](protocol/fitting.md#fitting-expansion-milestone-6-tech-i-capacitor-boosters)
- <a id="fitting-expansion-milestone-7-unrestricted-microwarpdrives-across-tiers"></a>[Fitting expansion milestone 7: unrestricted microwarpdrives across tiers](protocol/fitting.md#fitting-expansion-milestone-7-unrestricted-microwarpdrives-across-tiers)
- <a id="fitting-expansion-milestone-8-passive-tier-backfill"></a>[Fitting expansion milestone 8: passive tier backfill](protocol/fitting.md#fitting-expansion-milestone-8-passive-tier-backfill)
- <a id="fitting-expansion-milestone-9-capacitor-booster-tier-backfill"></a>[Fitting expansion milestone 9: capacitor-booster tier backfill](protocol/fitting.md#fitting-expansion-milestone-9-capacitor-booster-tier-backfill)
- <a id="fitting-expansion-milestone-10-armor-repair-and-hardener-tier-backfill"></a>[Fitting expansion milestone 10: armor-repair and hardener tier backfill](protocol/fitting.md#fitting-expansion-milestone-10-armor-repair-and-hardener-tier-backfill)
- <a id="fitting-expansion-milestone-11-shield-booster-tier-validation-and-skill-ownership"></a>[Fitting expansion milestone 11: shield-booster tier validation and skill ownership](protocol/fitting.md#fitting-expansion-milestone-11-shield-booster-tier-validation-and-skill-ownership)
- <a id="fitting-expansion-milestone-12-afterburner-tier-validation"></a>[Fitting expansion milestone 12: afterburner tier validation](protocol/fitting.md#fitting-expansion-milestone-12-afterburner-tier-validation)
- <a id="fitting-expansion-milestone-13-ordinary-ore-laser-tiers-residue-and-rounding"></a>[Fitting expansion milestone 13: ordinary ore-laser tiers, residue and rounding](protocol/fitting.md#fitting-expansion-milestone-13-ordinary-ore-laser-tiers-residue-and-rounding)
- <a id="fitting-expansion-milestone-14-hybrid-turret-and-charge-tiers"></a>[Fitting expansion milestone 14: hybrid turret and charge tiers](protocol/fitting.md#fitting-expansion-milestone-14-hybrid-turret-and-charge-tiers)
- <a id="fitting-expansion-milestone-15-projectile-turrets-and-ammunition"></a>[Fitting expansion milestone 15: projectile turrets and ammunition](protocol/fitting.md#fitting-expansion-milestone-15-projectile-turrets-and-ammunition)
- <a id="fitting-expansion-milestone-16-energy-turrets-and-persistent-frequency-crystals"></a>[Fitting expansion milestone 16: energy turrets and persistent frequency crystals](protocol/fitting.md#fitting-expansion-milestone-16-energy-turrets-and-persistent-frequency-crystals)
- <a id="fitting-expansion-milestone-17-local-hull-repairers-across-tiers"></a>[Fitting expansion milestone 17: local hull repairers across tiers](protocol/fitting.md#fitting-expansion-milestone-17-local-hull-repairers-across-tiers)
- <a id="fitting-expansion-milestone-18-missile-fitting-ammunition-and-owner-modifiers"></a>[Fitting expansion milestone 18: missile fitting, ammunition and owner modifiers](protocol/fitting.md#fitting-expansion-milestone-18-missile-fitting-ammunition-and-owner-modifiers)
- <a id="fitting-expansion-milestone-19-missile-motion-swept-contact-and-native-balls"></a>[Fitting expansion milestone 19: missile motion, swept contact and native balls](protocol/fitting.md#fitting-expansion-milestone-19-missile-motion-swept-contact-and-native-balls)
- <a id="fitting-expansion-milestone-20-paid-missile-firing-and-shared-npc-impacts"></a>[Fitting expansion milestone 20: paid missile firing and shared NPC impacts](protocol/fitting.md#fitting-expansion-milestone-20-paid-missile-firing-and-shared-npc-impacts)
- <a id="fitting-expansion-milestone-21-scripted-tracking-guidance-and-sensor-modules"></a>[Fitting expansion milestone 21: scripted tracking, guidance and sensor modules](protocol/fitting.md#fitting-expansion-milestone-21-scripted-tracking-guidance-and-sensor-modules)
- <a id="fitting-expansion-milestone-22-tackle-and-warp-navigation"></a>[Fitting expansion milestone 22: tackle and warp navigation](protocol/fitting.md#fitting-expansion-milestone-22-tackle-and-warp-navigation)

### Tech I hull expansion

- <a id="tech-i-hull-expansion-milestone-1"></a>[Tech I hull expansion milestone 1](protocol/hulls.md#tech-i-hull-expansion-milestone-1)
- <a id="tech-i-hull-expansion-milestone-2"></a>[Tech I hull expansion milestone 2](protocol/hulls.md#tech-i-hull-expansion-milestone-2)
- <a id="tech-i-hull-expansion-milestone-3"></a>[Tech I hull expansion milestone 3](protocol/hulls.md#tech-i-hull-expansion-milestone-3)
- <a id="tech-i-hull-expansion-milestone-4"></a>[Tech I hull expansion milestone 4](protocol/hulls.md#tech-i-hull-expansion-milestone-4)
- <a id="milestone-4-stock-client-observations"></a>[Milestone 4 stock-client observations](protocol/hulls.md#milestone-4-stock-client-observations)

### Architecture and typed boundaries

- <a id="attribute-based-di-acceptance"></a>[Attribute-based DI acceptance](protocol/architecture.md#attribute-based-di-acceptance)
- <a id="named-protocol-models-shared-session-plumbing"></a>[Named protocol models: shared session plumbing](protocol/architecture.md#named-protocol-models-shared-session-plumbing)
- <a id="named-protocol-models-inventory-and-docked-services"></a>[Named protocol models: inventory and docked services](protocol/architecture.md#named-protocol-models-inventory-and-docked-services)
- <a id="named-protocol-models-flight-and-combat"></a>[Named protocol models: flight and combat](protocol/architecture.md#named-protocol-models-flight-and-combat)
- <a id="named-protocol-models-market-industry-skills-and-brain"></a>[Named protocol models: market, industry, skills and brain](protocol/architecture.md#named-protocol-models-market-industry-skills-and-brain)
- <a id="named-protocol-models-final-audit"></a>[Named protocol models: final audit](protocol/architecture.md#named-protocol-models-final-audit)
- <a id="gameplay-boundary-milestone-1"></a>[Gameplay boundary milestone 1](protocol/architecture.md#gameplay-boundary-milestone-1)
- <a id="session-orchestration-milestone-2"></a>[Session orchestration milestone 2](protocol/architecture.md#session-orchestration-milestone-2)
- <a id="navigation-boundary-milestone-3"></a>[Navigation boundary milestone 3](protocol/architecture.md#navigation-boundary-milestone-3)
- <a id="inventory-boundary-milestone-4"></a>[Inventory boundary milestone 4](protocol/architecture.md#inventory-boundary-milestone-4)
- <a id="manufacturing-boundary-milestone-5"></a>[Manufacturing boundary milestone 5](protocol/architecture.md#manufacturing-boundary-milestone-5)
- <a id="architecture-audit-milestone-1"></a>[Architecture audit milestone 1](protocol/architecture.md#architecture-audit-milestone-1)
- <a id="architecture-audit-milestone-2"></a>[Architecture audit milestone 2](protocol/architecture.md#architecture-audit-milestone-2)
- <a id="architecture-audit-milestone-3"></a>[Architecture audit milestone 3](protocol/architecture.md#architecture-audit-milestone-3)
- <a id="architecture-audit-milestone-4"></a>[Architecture audit milestone 4](protocol/architecture.md#architecture-audit-milestone-4)
- <a id="architecture-audit-milestone-5"></a>[Architecture audit milestone 5](protocol/architecture.md#architecture-audit-milestone-5)
- <a id="architecture-audit-milestone-6"></a>[Architecture audit milestone 6](protocol/architecture.md#architecture-audit-milestone-6)
- <a id="architecture-audit-milestone-7"></a>[Architecture audit milestone 7](protocol/architecture.md#architecture-audit-milestone-7)
- <a id="architecture-audit-milestone-8"></a>[Architecture audit milestone 8](protocol/architecture.md#architecture-audit-milestone-8)
- <a id="architecture-audit-milestone-9"></a>[Architecture audit milestone 9](protocol/architecture.md#architecture-audit-milestone-9)
- <a id="architecture-audit-milestone-10"></a>[Architecture audit milestone 10](protocol/architecture.md#architecture-audit-milestone-10)
- <a id="architecture-audit-milestone-11-wallet-header-duplication"></a>[Architecture audit milestone 11: wallet header duplication](protocol/architecture.md#architecture-audit-milestone-11-wallet-header-duplication)
- <a id="architecture-audit-milestone-12"></a>[Architecture audit milestone 12](protocol/architecture.md#architecture-audit-milestone-12)

### Session performance and resident state

- <a id="resident-system-motion-and-npc-acquisition-november-5-2026"></a>[Resident system motion and NPC acquisition (November 5, 2026)](protocol/performance.md#resident-system-motion-and-npc-acquisition-november-5-2026)
- <a id="ten-brutix-comparison"></a>[Ten-Brutix comparison](protocol/performance.md#ten-brutix-comparison)
- <a id="resident-monitoring-and-scene-views-november-5-2026"></a>[Resident monitoring and scene views (November 5, 2026)](protocol/performance.md#resident-monitoring-and-scene-views-november-5-2026)
- <a id="ten-brutix-comparison-1"></a>[Ten-Brutix comparison](protocol/performance.md#ten-brutix-comparison-1)
- <a id="live-combat-objects-and-ten-brutix-replay-november-5-2026"></a>[Live combat objects and ten-Brutix replay (November 5, 2026)](protocol/performance.md#live-combat-objects-and-ten-brutix-replay-november-5-2026)
- <a id="same-build-ram-sql-versus-object-comparison"></a>[Same-build RAM SQL versus object comparison](protocol/performance.md#same-build-ram-sql-versus-object-comparison)
- <a id="resident-world-checkpoints-and-ten-brutix-replay-november-5-2026"></a>[Resident world checkpoints and ten-Brutix replay (November 5, 2026)](protocol/performance.md#resident-world-checkpoints-and-ten-brutix-replay-november-5-2026)
- <a id="same-build-ten-brutix-comparison"></a>[Same-build ten-Brutix comparison](protocol/performance.md#same-build-ten-brutix-comparison)
- <a id="ten-brutix-grouped-gun-benchmark-november-5-2026"></a>[Ten-Brutix grouped-gun benchmark (November 5, 2026)](protocol/performance.md#ten-brutix-grouped-gun-benchmark-november-5-2026)
- <a id="resident-ship-and-turret-fits-november-5-2026"></a>[Resident ship and turret fits (November 5, 2026)](protocol/performance.md#resident-ship-and-turret-fits-november-5-2026)
- <a id="repeated-turret-cycle-work-november-5-2026"></a>[Repeated turret cycle work (November 5, 2026)](protocol/performance.md#repeated-turret-cycle-work-november-5-2026)
- <a id="fitted-combat-catalog-reads-november-5-2026"></a>[Fitted combat catalog reads (November 5, 2026)](protocol/performance.md#fitted-combat-catalog-reads-november-5-2026)
- <a id="shared-session-performance-diagnostics-november-5-2026"></a>[Shared session performance diagnostics (November 5, 2026)](protocol/performance.md#shared-session-performance-diagnostics-november-5-2026)
- <a id="startup-protocol-and-milestones"></a>[Startup protocol and milestones](protocol/performance.md#startup-protocol-and-milestones)
- <a id="shared-session-scheduling"></a>[Shared session scheduling](protocol/performance.md#shared-session-scheduling)
- <a id="batched-npc-motion-and-catalog-inputs"></a>[Batched NPC motion and catalog inputs](protocol/performance.md#batched-npc-motion-and-catalog-inputs)

### Compatibility and route audits

- <a id="client-rpc-backlog-inventory-november-5-2026"></a>[Client RPC backlog inventory (November 5, 2026)](protocol/compatibility.md#client-rpc-backlog-inventory-november-5-2026)
- <a id="localization-qa-wrap-compatibility-november-5-2026"></a>[Localization QA wrap compatibility (November 5, 2026)](protocol/compatibility.md#localization-qa-wrap-compatibility-november-5-2026)
- <a id="console-logging-cleanup-november-5-2026"></a>[Console logging cleanup (November 5, 2026)](protocol/compatibility.md#console-logging-cleanup-november-5-2026)
- <a id="placeholder-route-implementation-november-4-2026"></a>[Placeholder route implementation (November 4, 2026)](protocol/compatibility.md#placeholder-route-implementation-november-4-2026)
- <a id="gameplay-rejection-dialogs-november-2-2026"></a>[Gameplay rejection dialogs, November 2, 2026](protocol/compatibility.md#gameplay-rejection-dialogs-november-2-2026)
