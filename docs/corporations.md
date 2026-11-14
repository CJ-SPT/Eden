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
| Shared ISK wallets, journals and bounty tax collection | Published; 309 focused tests and required checks passed; CEO/Director startup correction passed 136 focused tests |
| Corporate LP/EverMark wallets | Published; 191 focused tests and required checks passed |
| Bills panel reads/preferences and corporate market history reads | Published; 203 focused tests and required checks passed; invoice sources/corporate trading remain separate |
| Corporation inventory ownership storage | Published; 176 focused tests and required checks passed |
| Offices and corporation asset access | Implemented: rentals, seven hangar divisions, transfers and asset reports; recurring billing/impound recovery deferred |
| Member communications and recruitment services | Direct invitations implemented; bulletin editing, advertisements and welcome mail pending |
| Share holdings, shareholders and transfers | Published; 198 focused tests and required checks passed |
| Voting, share issuance/dividends and CEO succession/closure | Pending |

See [corporation protocol findings](protocol/corporations.md) for native call shapes and milestone validation evidence, and [inventory ownership](inventory-ownership.md) for the storage boundary. This table records implemented scope, not live client acceptance.
