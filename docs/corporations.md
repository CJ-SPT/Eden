# Standalone corporation implementation

[Documentation index](README.md) / [Corporation protocol records](protocol/corporations.md)

The requested scope is standalone player corporations. Alliances and corporation wars are separate work.

Each milestone includes typed rules, authenticated native calls, persistence, focused automated checks and a verified push before the next milestone starts. Native client acceptance is disabled for agents.

| Milestone | Status |
| --- | --- |
| Creation, identity, applications, joining/leaving | Published; [direct invitations](protocol/corporations.md#direct-invitations-and-corporation-window-reads-november-14-2026) now use resident applications and checkpoints |
| Login and corporation-window compatibility | Published |
| Persistent settings, logos and division names | Published; 116 focused tests and required checks passed |
| Delegated roles, titles and member administration | Published; 177 focused tests and required checks passed |
| Member search and activity tracking | Typed queries, title/implied-role searches, online status and persistent session times implemented |
| Shared ISK wallets, journals and bounty tax collection | Published; 309 focused tests and required checks passed; CEO/Director startup correction passed 136 focused tests |
| Corporate LP/EverMark wallets | Published; 191 focused tests and required checks passed |
| Bills panel reads/preferences and corporate market history reads | Published; 203 focused tests and required checks passed; invoice sources/corporate trading remain separate |
| Corporation inventory ownership storage | Published; 176 focused tests and required checks passed |
| Offices and corporation asset access | Implemented: rentals, seven hangar divisions, transfers and asset reports; recurring billing/impound recovery deferred |
| Member communications and recruitment services | Direct invitations, bulletins, automatic welcome mail and paid recruitment advertisements implemented |
| Share holdings, shareholders and transfers | Published; 198 focused tests and required checks passed |
| Dividends and CEO succession | Implemented: exact member/shareholder payments and docked CEO transfer |
| Corporation closure | Implemented: member departures, archived identity/assets and released office slots |
| General polls and share-creation votes | Implemented: weighted ballots, deadlines, CEO sanction and persistent share supply |
| Member-expulsion votes | Implemented: shareholder protection, suspended roles, CEO sanctions and persistent docked-removal queue |
| Bulk member removal | Implemented: resident batch settlement, native mixed results, current Director permissions and existing removal protections |
| Native administration and inventory blockers | Implemented: member audit reads, current membership dates, headquarters relocation, Goals Deliveries queries and corporate Deliveries binding |
| CEO elections | Implemented: personal-share eligibility, incumbent suspension, automatic succession and a persistent successor authority delay |
| Blueprint lock/unlock votes | Pending |

See [corporation protocol findings](protocol/corporations.md) for native call shapes and milestone validation evidence, and [inventory ownership](inventory-ownership.md) for the storage boundary. This table records implemented scope, not live client acceptance.

The maintainer confirmed that the corporation window loads after the November 14 title-row correction. Completion work
now covers the remaining standalone tabs and actions, including recruitment, member queries and governance. Alliances
and corporation wars remain explicitly outside this request.

Implementation resumed after the member-expulsion vote milestone. Remaining standalone work includes blueprint voting,
full historical auditing/decorations, outstanding home settings, recent combat records, ordinary removal queues
and office billing/impound recovery. The complete corporation window is not yet implemented.
