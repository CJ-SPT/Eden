# Cooperative fleets

[Documentation index](../README.md) / [Protocol index](../protocol.md)

## Command Bursts, November 13, 2026

Eden implements 18 published Command Burst modules and 19 charges from build 3662168. The six families are Armor, Shield,
Information, Skirmish, Mining Foreman and Expedition. Normal fitting and loading enforce catalog hull, slot, skill, charge and
online limits. Leadership, Wing Command, Fleet Command, family skills, admitted hull bonuses and Command Processor rigs
enter the existing fitting graph. Command Bursts require Omega to activate.

Each pulse consumes one loaded charge and pays the fitted capacitor cost. It starts a weapons timer and saves the module
cycle deadline. The source ship and eligible fleetmates inside the fitted surface-distance radius receive independently timed
dogma effects. Recipients are selected when each pulse fires. A received effect lasts through later range or fleet changes
and same-system warp. Docking and system changes clear it. High-security pulses skip fleetmates engaged with a third party
under the implemented limited-engagement rule.

The fitting graph uses the strongest active value per catalog collection and falls back to a weaker unexpired value when
that stronger effect ends. Collection modifiers affect the ship, selected fitted modules, probe profiles and mining-crystal
wear. These modifiers use the catalog's stacking rules alongside ordinary equipment. The resident world indexes deadlines
and invalidates the recipient's fitting cache at expiry. Checkpoints retain payments and cooldowns. Startup reconciliation
clears timed bonuses and repeat state.

The native route uses `dogmaIM.Activate` and `Deactivate`, effects 6732–6736, charge effect 6737, and the pinned
`client/dbuffcollections` selectors and aggregation. SetState and `OnDbuffUpdated` carry collection/value/expiry state.
The sender receives `OnFleetBoosted`, charge quantity and capacitor updates. The flight stream carries the range-scaled
`OnSpecialFX` pulse and the individual recipient effects.

Inert inspection of the installed V24.02 client archive
(`2DBC29987D01515F4F40C319F89F0276F05AC3D175D79776A03F1F8E2BAB5C6A`) found the five graphic families in
`dbuff/common/baseBurstEffect.pyj`, the update consumer in `dbuff/client/incomingDbuffTracker.pyj`, and the source
notification in `eve/client/script/parklife/dbuffClient.pyj`. The archive differs from the recorded pinned client baseline.
The build-3662168 catalog establishes module and charge values. EveJS 261108.1 was consulted for recipient selection,
pulse behavior and client state. Its local command-burst runtime has SHA-256
`FA64CC42ACF5361EFD96E41C3579739E60ABD7CB44857DEB886B57B90F319B49`.
[CCP Command Burst](https://support.eveonline.com/hc/en-us/articles/213817305-Command-Burst),
[CCP fleet boosting](https://www.eveonline.com/news/view/command-bursts),
[EVE University Command Bursts](https://wiki.eveuniversity.org/Command_Bursts),
[Carbon Destiny surface distance](https://github.com/carbonengine/destiny/blob/114e89fa584f0e1a81eb0a679aabf41af8870072/src/Thunkers.cpp#L1798).

Mindlink installation, tether and starbase force-field rules depend on their owning systems. Fleet hangars, ship
maintenance bays, micro-jump fields and titan Phenomena Generators retain their own milestones.

**Automated validation.** Tool and solution restore passed. The final build completed in 7.98 seconds with zero warnings
or errors. The rebased affected run passed 307 tests in 232.80 seconds and skipped one launcher fixture that requires
`EDEN_TEST_CLIENT_ROOT`. After the final visual and warp-safety changes, the focused `CommandBurstTests`, `TypedFlightTests`
and `CooperativeFleetTests` selection passed all 37 cases in 17.47 seconds. The burst cases cover real Ferox fitting,
charge compatibility, capacitor payment, fleet eligibility and range, exact expiry, fallback selection, checkpoint reopening,
and zero SQL commands, connection opens or fitting calculations across 12 warmed recipient reads. Both Python policy
suites passed (8 catalog and 9 formatting cases). Formatting verification passed across 1,977 files in 23.39 seconds.
The pinned fixture matched the Git blob byte-for-byte, and the refreshed
coverage entries came from offline production catalog exports. The full .NET suite was outside this validation.

**Live acceptance.** Not performed; agent client testing is disabled.

**Optional maintainer checks.** Fire a burst with a second pilot in the fleet. Verify the sphere graphic, both ships'
status indicators, and the indicator's expiry after the receiving pilot warps.

## Cooperative fleet foundation, November 7, 2026

Eden now supports invitation-based cooperative fleets through the pinned client's ordinary fleet interface. This is independent of `Docking.FleetHandler` and `IPilotFleet`, which still manage personal assembled ships.

### Membership and leadership

- `fleetObjectHandler.CreateFleet()` returns a pilot-scoped fleet object reference. The creator becomes fleet commander and boss, with one initial wing and squad.
- Native `fleetObjectHandler` resolve/bind requests name the fleet ID. Only a current member or the recipient of a live invitation may bind. An invited nonmember may accept/reject, but cannot read the roster, issue commands or inspect private MOTD data before accepting. Embedded first calls preserve success/None/failure and reference semantics.
- The bound object supports `Init(shipTypeID,setupName,adInfoData=None)`, `GetFleetID`, `GetInitState`, `GetWings`, `GetMembers`, `GetOptions`, `GetJoinRequests`, `GetMotd`, `UpdateMemberInfo`, `Invite`, `AcceptInvite`, `RejectInvite`, `LeaveFleet`, `KickMember`, `DisbandFleet`, `MoveMember`, `MakeLeader`, wing/squad creation/deletion/naming, `SetOptions`, `SetMotdEx`, `SetTakesFleetWarp` and `SendBroadcast`. `FinishMove` acknowledges already committed movement. `Reconnect` only acknowledges an existing current membership; it cannot restore a disconnected fleet slot.
- Fleet IDs and subgroup IDs share a fresh random namespace for each host instance. Guessing an ID or retaining a former reference grants no authority. Invitation acceptance rechecks the issuer's current authority, slot availability, target membership and the two-minute deadline. Concurrent accepts create one membership and one join event. Ship type metadata is checked against the selected ship, while native None is accepted when the client has not loaded its active hull yet.
- Fleet commander/wing commander/squad commander/member roles use the pinned values **1/2/3/4**. Boss is the independent job bit **2**. Boss transfer does not silently promote command rank. Commanders may manage only subordinate positions inside their scope; members cannot promote themselves or others. Free movement permits self-assignment as a member or into an empty squad-command position, without granting wing or fleet command. Only the boss may disband, transfer boss authority, change fleet options/MOTD, create wings or remove/rename hierarchy groups. A wing commander may create squads in their own wing. Nonempty groups cannot be deleted; their members must be moved first.
- The limits are **256 members**, **25 wings**, **25 squads per wing**, ten-character subgroup names and a 4,000-character MOTD. The roster and wing/squad replies use named immutable snapshots and native KeyVal/dictionary forms. Mutation rejection leaves prior snapshots unchanged.

`OnFleetInvite(fleetID,inviterID,msgName,msgDict)` uses the pinned **AskJoinFleet** question (dialog 82, body 260379, title 260378), rather than an invented message key. The client supplies the inviter's formatted `name`. Other native notifications include `OnFleetJoin`, `OnFleetLeave`, the 13-field `OnFleetMemberChanged`, wing/squad changes, option/MOTD changes and initial `OnFleetStateChange` with empty target tags. Session fields include `fleetid`, `wingid`, `squadid`, `fleetrole` and an unset `fleetbooster`. Session deltas precede successful RPC replies using the existing output path. Docking, gates, translocation, boarding and capsule adoption preserve the fleet identity held by the connection's flight session.

### Broadcasts, chat and coordinated travel

Broadcasts support the native Location, HoldPosition, TravelTo, JumpTo, AlignTo, NeedBackup, WarpTo, HealCapacitor, HealShield, HealArmor, Target, EnemySpotted, InPosition and HealTarget names. `SendBroadcast` addresses the fleet; `fleetMgr.BroadcastToSystem` and `BroadcastToBubble` restrict location. Down/up/all directions use the current command hierarchy. The sender and system come from authoritative presence, not caller-provided identity. A one-second sender limit bounds broadcast traffic. `OnFleetBroadcast` retains its six native arguments: name, scope, character, system, optional item and optional type. These are coordination messages; repair/target broadcasts do not activate modules or grant combat/loot rights.

The fleet chat room is `fleet_<fleetID>`. It appears in normal system-channel access, with live roster/message behavior through the existing TLS chat service. Both membership changes and XMPP send/join/info operations reconcile current fleet membership. Outbound fleet messages recheck each recipient against the fleet owner, so kicking a pilot revokes sending and receiving even before their next game-session update. Corporation, Local and private chat behavior remains separate.

`beyonce.CmdWarpToStuff` now accepts `fleet=True`/`1` for supported item and bookmark destinations. The stock `fleetFormationSettings` dictionary is validated: only **POINT=0** is supported, with the native spacing/size fields accepted as unused point-formation metadata. Other geometric formations are rejected before movement. The commander starts through the existing warp operation; successful commands then queue one order for each eligible subordinate on the current observer grid. That grid uses `NavigationDefinitions.MaximumTargetDistance`, currently eight million metres. Wing/squad commands stay inside their command scope. Members can opt out. Docked pilots and pilots in another system are excluded.

Each recipient consumes an order once through its own ordinary flight operation. That preserves alignment, capacitor/warp physics, tackle, Crimewatch and travel restrictions. Expired orders, changed membership/rank, opt-outs, changed ships, departure from the issuing grid and newer personal navigation invalidate pending work. The order captures its issuing origin: the commander's subsequent legitimate warp does not make later observers miss the order. State refreshes do not cancel commands. A member's rejection does not replay the command or move that ship by force. Fleet warp does not automatically jump gates, dock, bypass disruption or teleport members.

The native `"char"` warp subject supports personal and commanded warps to a current fleet member's observed coordinates in the same system. Docked, departed, foreign and other-system members are rejected. `beyonce.GetFleetLocationAndInfo()` returns membership-checked same-system position/type records with the native `pos` tuple. Point bookmarks are resolved under the issuing pilot's existing bookmark permissions; other pilots receive an authorized destination, not access to the private bookmark folder.

### Runtime and limits

Typed rules, memberships, invitations, immutable snapshots and events live in `Eden.Gameplay/Fleets`. The singleton World owner serializes operations without retaining client objects, database connections, fits or callbacks. Protocol owns fleet bindings, native models, session fields and delivery into ordinary flight operations. Position/type/corporation changes update typed presence; unchanged monitoring uses no SQL or fitting calculations. Per-pilot event queues are bounded at 256 entries and overflow to a roster resynchronization. Deliveries retain their originating fleet ID, preventing old events from populating a newly joined fleet. Joining also clears old membership events and already queued fleet notifications, including an immediate rejoin to the same fleet. Member-change and overflow events capture their required roster metadata under the owner; notification delivery does not reload a roster that a concurrent kick may already have revoked.

Fleets are **transient online groups** in this milestone. Logging off/disconnecting removes that member, preserves their assets and deterministically transfers boss authority to the highest-ranked remaining member, then character ID. The final departure destroys the fleet. Server restart discards groups and pending invitations; no world schema change or durable progression is introduced. Reconnection grace, offline invitations, fleet finder adverts, stored setups, mass invitation/movement, watchlist damage updates, composition/leadership-skill reporting, command bonuses, geometric formations, automatic reward/loot sharing and player remote-support mechanics remain separate work. Empty finder reads, initial damage registration and target-tag state are compatibility reads, not claims that those deferred features work.

### Sources and validation

Inert source inspection used build **3662168** `code.ccp`, SHA-256 `37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368`: `parklife/fleetSvc` creation/init/invite/join/leave/role/broadcast/session paths; `eveMoniker.GetFleet`; `evefleet.const`, `GetNewMemberOptOutsKeyval`, hierarchy/menu consumers and fleet location readers; `remote/michelle.SetFleetFormationSettings`; movement helpers and `MenuSvc.WarpToMember`/`WarpFleetToMember`; `warpSubjects.CHAR`; and the XMPP fleet channel consumers. The pinned catalog's `client/dialogs` row confirms AskJoinFleet. The stock options request copies all three option fields, and docked ship metadata may be None; both forms are retained.

Carbon does not provide these fleet gameplay services in the consulted reference inventory. Existing Blue KeyVal/list/dictionary/tuple/reference adapters reuse [Blue revision fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae](https://github.com/carbonengine/blue/blob/fcbf0c2e7f092568adb82edb7b8fb97df75ad4ae/src/Marshal.cpp). Consulted the local pinned [Destiny Ballpark.cpp at 27ee40b38a563d29054b32f64c3ebc87fabaec1f](https://github.com/carbonengine/destiny/blob/27ee40b38a563d29054b32f64c3ebc87fabaec1f/src/Ballpark.cpp), `WarpTo`, including finite-coordinate validation, redundant-order handling and ordinary alignment state. Group commands reuse Eden's existing single-ship warp pipeline; no Blue codec or Destiny binary format changed. No client scripts were executed and no normal save was modified.

**Automated validation:** the final sequence passed tool restore, solution restore, build (zero warnings/errors), **443 affected .NET tests** (including 24 fleet cases) in **145.94 seconds**, both Python policy suites (3 catalog and 9 formatting cases), formatting verification (925 files and the 140-character C# width check), documentation links/anchors/UTF-8 checks and `git diff --check`. The complete sequence took approximately three minutes on the integrated `def8fc2` base. The complete affected filter also passed before the final event-snapshot adjustment; the recorded final run includes that adjustment and its owner-captured metadata assertions. Development began with exact filter `FullyQualifiedName=Eden.Protocol.Tests.PilotSessions.CooperativeFleetTests.NativeCreateInviteAcceptAndLeaveKeepReferencesRosterAndSessionFieldsConsistent`. The expanded membership/chat/bookmark/flight boundary filter passed **115 tests in 29 seconds** before adding native fleet-member warp and real tackle coverage. An old bookmark regression expected the former unsupported message; it now asserts the unaffiliated-pilot rejection while preserving unchanged-flight/bookmark checks. The initial tackle fixture lacked its required module foreign key; the corrected case uses a real fitted module and ordinary tackle activation. Native MOTD assertions decode byte strings explicitly. The first final affected run passed 440 of 442 cases, with the two six-leg translocation/chat scenarios exhausting the test client's 15-second connection-wide timeout under parallel load. Both cases passed in isolation on this branch and unchanged `def8fc2`. The synthetic client now retains the 15-second handshake limit and applies a fresh 15-second deadline to each read/write, so a long healthy connection does not expire between operations; gameplay transport timeouts and test assertions are unchanged. Full .NET regression testing was not requested or performed.

The final affected filter (not the full .NET suite) was:

```powershell
$fleetFilter = @(
    'FullyQualifiedName~FleetRulesTests'
    'FullyQualifiedName~CooperativeFleetTests'
    'FullyQualifiedName~Eden.Protocol.Tests.Chat'
    'FullyQualifiedName~Eden.Protocol.Tests.Flight'
    'FullyQualifiedName~PilotBindingsTests'
    'FullyQualifiedName~TypedSessionTests'
    'FullyQualifiedName~PilotServicesTests'
    'FullyQualifiedName~BookmarkServiceTests'
    'FullyQualifiedName~PilotBookmarksTests'
    'FullyQualifiedName~CorporationAdministrationTests'
    'FullyQualifiedName~CorporationMembershipTests'
    'FullyQualifiedName~CorporationStorageTests'
    'FullyQualifiedName~TackleTests'
    'FullyQualifiedName~Crimewatch'
    'FullyQualifiedName~DetachedCheckpointTests'
    'FullyQualifiedName~Eden.Protocol.Tests.Hosting'
    'FullyQualifiedName~Eden.Protocol.Tests.Serialization'
) -join '|'
.\tools\Test-Feature.ps1 -Filter $fleetFilter
```

**Live acceptance:** Not performed; agent client testing is disabled. Native fleet-window rendering, invitation dialogs, chat presentation and coordinated travel feel remain unverified.

**Optional maintainer checks:** create/invite with two clients and inspect hierarchy/leadership; verify fleet chat and travel broadcasts; try point fleet warp with one member opted out.
