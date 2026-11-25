# Standalone corporation implementation

[Documentation index](README.md) · [Corporation protocol records](protocol/corporations.md)

| Feature | Supported actions |
| --- | --- |
| [Membership](protocol/corporations.md#player-corporation-applications-and-membership) | Create a corporation, apply, invite pilots, accept offers, leave and remove members. |
| [Delegated roles and titles](protocol/corporations.md#delegated-corporation-roles-and-titles) | Assign roles and titles, manage member access and apply the role-removal wait. |
| [Member administration](protocol/corporations.md#administration-and-inventory-blockers-november-15-2026) | Search members, read activity and membership dates, move headquarters, and manage docked or queued removals. |
| [Communication and recruitment](protocol/corporations.md#bulletins-and-welcome-mail-november-14-2026) | Publish bulletins and recruitment adverts, send welcome mail and [schedule corporation events](protocol/social.md#corporation-calendar-events). |
| [Wallets](protocol/corporations.md#shared-corporation-isk-wallets-and-bounty-taxation) | Seven shared ISK divisions, journals, transfers, bounty tax, LP and EverMark balances. The client can read bills and corporate market history and save autopay preferences. |
| [Office and asset storage](protocol/corporations.md#corporation-offices-and-shared-storage-november-6-2026) | Rent offices, use division hangars, transfer and stack assets by role, and read corporate asset reports. |
| [Shares and governance](protocol/corporations.md#general-and-share-creation-votes-november-15-2026) | Transfer shares, pay dividends, hold polls and share-creation votes, vote on member expulsion and blueprint locks, elect successors and close a corporation. |
| [Corporation and alliance wars](protocol/corporations.md#corporation-and-alliance-wars) | Declare and fund wars, accept mutual invitations, hire defender allies, negotiate surrender, and review War Report losses. |

Recurring office billing, impound recovery and corporate market orders remain open. See the
[inventory ownership notes](inventory-ownership.md) for the schema-78 migration and isolation rules. See
[alliances](protocol/alliances.md), [sovereignty](protocol/sovereignty.md), and
[Faction Warfare organization enlistment](protocol/faction-warfare.md#organization-enlistment-and-militia-communications--november-20-2026)
for related organization features.

The maintainer confirmed that the corporation window loaded after the November 14 title-row correction.
[Title-row correction and acceptance record](protocol/corporations.md#corporation-title-row-decoding-november-14-2026)
