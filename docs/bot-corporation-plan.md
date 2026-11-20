# Bot corporation recruitment, relocation, and identity

[Documentation index](README.md#long-term-design) · [Current bot corporation behavior](capsuleer-population.md#bot-led-player-corporations)

Implementation records cover [identities](protocol/capsuleers.md#bot-corporation-identities),
[recruitment](protocol/capsuleers.md#autonomous-corporation-recruitment), [relocation](protocol/capsuleers.md#bot-corporation-relocation),
and [retention](protocol/capsuleers.md#bot-corporation-retention).

## Why rosters are local today

At `a1437dc4`, `CapsuleerCorporations.Place` looks for employers in the pilot's provisioning community and its immediate
gate neighbors. It prefers the home system, then the lowest corporation ID, and enrolls the pilot immediately if there
is room under both the 12-member bot target and the charter's member limit. Automatic placement starts with eligible
school-corporation pilots with no player-corporation employment history. Separate invitation handling also checks the
one-jump radius. Community preparation runs in occupied systems, and miners and haulers can also place at a dock.
[Placement](../src/Eden.World/Capsuleers/CapsuleerCorporations.cs) ·
[Invitation review](../src/Eden.World/Corporations/CorporationMembershipStore.Capsuleers.cs) ·
[Background placement](../src/Eden.World/Capsuleers/CapsuleerScheduler.Corporations.cs)

Membership changes affiliation and employment history without giving the recruit a travel assignment or a new operating
base. Names combine the headquarters system, one of eight brands, and a business suffix. The provisioning community
and slot also appear in admission and worker selection, so changing a bot's community would change more than its home.
[Membership](../src/Eden.World/Corporations/CorporationMembershipStore.cs) ·
[Name generator](../src/Eden.Gameplay/Capsuleers/CapsuleerCorporationRules.cs) ·
[Population models](../src/Eden.Data/World/Capsuleers/SavedCapsuleerPopulation.cs)

## What a pilot should do

A hauler based several jumps away can discover a corporation that needs a courier through an active advert or a shipment
it helped deliver. It compares the work and travel risk with its current earnings, applies, gets an offer, and decides
at a safe dock after delivering its current load. After joining, it flies its fitted ship and the supplies it can carry
to the advertised base. Its next regular freight run starts there. A local miner can join the same corporation and stay
at the mining site if that is where the corporation needs it. Other pilots stay independent when the available offers
leave them worse off. EVE players use adverts, public fleets, and referrals to discover corporations and consider activity,
taxes, and infrastructure when deciding where to join. [Finding a corporation](https://wiki.eveuniversity.org/Find_a_Corporation) ·
[Applications and invitations](https://wiki.eveuniversity.org/Recruiting_corporation_members)

## Delivery milestones

### 1. Give new corporations distinct identities

Generate identities from a world seed and stable founder identity at charter creation. Give founders several plausible
styles, such as a two-word name, a founder-led company, a cooperative, or an understated single word. Illustrative names
include **Glasswake** `[GLSW]`, **Orchard Seven** `[ORCH7]`, **Vey & Company** `[VEYCO]`, and **Second Shift Cooperative**
`[2SHFT]`. Let culture or purpose influence the vocabulary. Put the activity and operating location in the description. Draw readable
ticker candidates from the name and vary the existing native palette independently. Save the chosen details when the
charter is created, including when the next choice is needed after a name or ticker collision.

Use the same validation and case-insensitive NPC and player collision checks as player-created charters. Check the
50-character name and five-character ASCII alphanumeric ticker limits, and bound retries. Use a fixed-seed sample of
500 founding candidates to report distinct full names, naming styles, common repeated terms, and collision retries.
Exercise a forced collision, concurrent founding, and checkpoint reopen. Show that moving headquarters preserves the
saved identity. [Founding and uniqueness](../src/Eden.World/Corporations/PlayerCorporationStore.cs)

New charters get the new generator on rollout. Existing corporations keep their identity by default, including those
with human members. A separate administrator-selected refresh can cover confirmed legacy-generated, bot-led charters
with no human members. Preview old and proposed names, keep the original ticker and corporation ID, and verify name
search and owner-name invalidation before enabling that action. Verify that references, employment, assets, and alliance
membership survive an approved rename. Human-led charters are ineligible for the refresh.

### 2. Replace placement with recruitment decisions

- Give bot-led corporations recruiting priorities based on actual vacancies. A miner shortfall, an unserved freight
  route, or a retained defense commitment can open a role. Use regular paid recruitment adverts where a corporation can
  afford them. Qualified recruiters can also invite pilots after completed shared work. Use bounded recent contacts from
  actual activities such as freight or fleet work. Start with adverts and ordinary invitations and add referrals when a
  shared operation can supply that evidence. Keep human-led corporations under their human recruiters' control.
- Give each pilot a short list from discoverable adverts, current invitations, and recent working relationships. Compare
  career fit and useful facilities with tax, replacement and travel costs, route safety, and the pilot's current work.
  Allow an applicant to stay where it is, prefer a nearby fit, or take a worthwhile offer several jumps away. Start with a
  regional advert search and a bounded route check. Measure recruit origin spread before tuning search reach. Proximity is
  a travel cost. Use stable, pilot-specific preferences to choose among similarly good offers without repeatedly
  rerolling the same choice.
- Use ordinary application, offer, invitation, rejection, withdrawal, and acceptance transitions. Persist staggered
  review deadlines and give bots at most one active autonomous application. At review and acceptance, recheck the open
  role, capacity, corporation leadership, departure permissions, and pilot commitments. Use the charter member limit as
  a hard limit and treat the initial bot roster target as a planning target. Expire or withdraw stale autonomous requests
  through existing transitions. Keep human application, invitation, and acceptance flows intact.
- Keep charter creation separate from enrolling neighbors. In established saves, a founder can use the existing eligible
  founding path when nearby pilots have an unmet need and no suitable employer. Leave room for small corporations and
  stagger charter reviews. In fresh worlds, start untied pilots on their existing career paths and require earned
  qualifications and the normal founding fee before enabling autonomous founding there. Coordinate that rollout with the
  fresh-world first-claim milestone. [Bot development milestones](bot-development-plan.md)

Cut over occupied-system preparation, docked miner and hauler work, and invitation settlement together so each bot has
one recruitment decision owner. Use the resident scheduler's active-play clock and bounded, indexed due work. Reuse
the membership operation inside the resident transaction and publish refreshes after commit. A distant player should
enable a recruit's review while the recruit's own system is empty. Recruitment sleeps with zero selected humans.
[Native invitation contract](protocol/corporations.md#direct-invitations-and-corporation-window-reads-november-14-2026) ·
[Recruitment implementation](protocol/capsuleers.md#bot-corporation-recruitment-and-gangs)

Replay a founder hiring at least two non-local pilots from distinct origin systems, including one from more than one jump
away, alongside a local hire and an independent pilot who declines an unsuitable offer. Prove that a newer charter with a
better role can win, a full or closed charter cannot accept, and a distant human invitation can be accepted for a good fit.
Cover two offers competing for the final place, a human CEO takeover, and reopening before review and after acceptance.
Compare application history, employment, member counts, access, and any human-facing notifications.

### 3. Move recruits and rebase their work

Keep the admission community and slot for provisioning. Use a distinct operating station for regular work. Start with the
corporation's NPC-station headquarters, or a station selected for that role near its operations. Pick a reachable station
with the needed services and access. Members whose work fits their existing station can stay there. A corporation office,
service access, legal headquarters, and a pilot's medical clone are separate concerns. Changing work base requires only
the work move. Defer any future medical move to the ordinary home-station operation and its access, fee, and cooldown.
[CCP corporation offices](https://support.eveonline.com/hc/en-us/articles/203209652-Corporation-Offices) ·
[Eden home-station mechanics](protocol/characters.md#persistent-home-stations-and-docked-clone-transfer)

When the offer is accepted, commit a relocation assignment if the pilot agreed to move. Enable offers that depend on a
move when this milestone lands. Store the assignment's source, destination, route progress, cargo custody, and next action
with the existing pilot state. Let active freight, escort, gang, corporate
job, or claim-defense duties finish or reach their normal handoff before departure. Use normal undock, warp, gate and
dock operations for the pilot's ship. Move owned supplies through ordinary inventory and cargo capacity rules. Excess
personal stock can stay in the old hangar for later pickup, sale, or a separately funded shipment. Corporate stock, jobs,
and orders remain at their existing locations under their current owners.

Commit the new work base at arrival, then replan the miner's belt and crew or the hauler's regular route against the
new base. Use the current ship location for observation and the new operating base for future work and recovery planning.
Refresh worker and observer state after commit in both affected systems. Audit origin-based population and traffic lookups
so migration neither duplicates pilots nor replenishes their original slots by accident. Defer or reroute when access or
route conditions change. Handle loss through existing ship replacement and cargo loss rules and reassess the move from the
pilot's resulting location. Let the member retain its current workable base if the move has to end.

Replay an observed origin, an unobserved gate leg, and an observed destination. Reopen both in flight and after docking.
Verify the same pilot and ship through the handoff, exact cargo and old hangar quantities, one committed work-base change,
and a subsequent mining or hauling cycle from the new base. Cover denied docking and ship loss, a protected job in progress,
and observation arriving during a saved leg. Use a distant selected human to drive the unobserved leg and verify pause and
resume when that player signs out and returns. [Offscreen work](protocol/capsuleers.md#offscreen-mining-belt-ratting-and-industry)

### 4. Add retention, later moves, and population checks

Reevaluate settled members after a meaningful change such as sustained lack of work, loss of access, or a stronger offer.
Require a sustained improvement and a minimum tenure before an autonomous employer change. Support authorized departures
from bot-led charters using existing CEO and role rules. Preserve a human-led corporation's authority over its roster and
use the same travel and asset rules for later moves. Reconsider a base on an actual change in corporate staging, such as
an accepted headquarters move or lost facility access. Stagger reviews and moves so a corporation can keep working while
members travel. Coordinate specialist departures with industry, defense and replacement staffing.

Compare matched baseline and proposed runs with multiple communities, several kinds of corporations, and one human playing
elsewhere. Record distinct origin systems per corporation, largest origin share, same-system and multi-jump hiring,
independent pilots, accepted offers followed by a move, time to first work at the new base, and repeat moves or departures.
Use distributions across multiple seeds and inspect both sparse and dense areas. Check that specialist staffing, paid work,
and alliance and sovereignty commitments keep progressing. Set pacing from these measurements before expanding rollout.

Measure due-work backlog age, jobs completed in distant systems, session-frame latency and allocations, and checkpoint
cost separately. Use the same workload for both revisions and assert that warmed recruitment and relocation decisions
open no world SQL connections and perform no unrelated fit calculations. Keep durable intents, deadlines, and membership
and work-base writes under the existing world owner and checkpoint generation. Publish only committed changes and test
that retries after a failed commit or checkpoint do not duplicate travel, cargo, applications, or employment.

## Validation and references

Start with exact filters in `CapsuleerCorporationTests` for current membership and invitation behavior, then extend the
focused coverage to `CorporationMembershipTests`, `CapsuleerBackgroundPreparationTests`, and `CapsuleerHandoffTests` as
their boundaries change. Include an established-save replay, a fresh-world replay when earned founding is enabled, and
human-visible reads for owner names, Local, station guests, and corporation employment. Record an existing-save baseline
before the cutover. Run the repository's milestone checks and record the focused filters, elapsed time, and checkpoint
results with each implementation. [Contributor testing policy](../AGENTS.md#testing) · [Validation guide](testing.md)

Eden's recorded client contracts target build 3662168. The local EVE.js reference, version `261108.1` targeting build
3396210, was checked for application transitions and advert discovery. `corpRegistryRuntime.js` separates recruiter
approval from applicant acceptance (SHA-256 `41d691387ff45dce3cf4680da40291557b868f0b3f589234d0eb244b77c4e35c`).
`corpRecProxyService.js` provides advert discovery (SHA-256 `bc88564d679e42ca52c18cd0099ff2a078a93648d7506f0d2d49ce282baacf45`).
The decision cadence, score, and relocation policy are Eden design choices. Use Eden's recorded native service contracts
and existing catalog route and station data to implement them. [Corporation protocol notes](protocol/corporations.md)
