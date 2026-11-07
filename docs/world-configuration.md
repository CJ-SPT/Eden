# Saved world configuration

[Documentation index](README.md)

Schema 99 adds immutable configuration revisions to each saved world. Upgrading captures its existing development supply, economic policies, adjusted prices, manufacturing indices and renewal schedule. Existing worlds retain their values; normal new-world defaults remain unchanged.

A draft saves as **pending restart**. Saving, restoring and importing use the same validation and optimistic generation check. Startup validates the pending revision before resource, market, combat and drone reconciliation. A rejected revision leaves the previous active revision in use and records its failure. Its history entry remains available; restoring a valid revision creates another pending entry. Revisions cannot be updated or deleted.

| Settings group | Editable server policies |
| --- | --- |
| Economy | Charge service costs; default manufacturing index. |
| Development supply | Testing market and initial wallet grants; institutional stock; testing stock/price; grant amount for future pilots. |
| Resources | Enable belt renewal and set its UTC hour. |
| NPC population | Enable new pirates, guards, convoys and mining populations independently. Existing living residents remain. |
| Progression | Grant catalog starting skills to future pilots. Native training formulas remain unchanged. |
| Simulation | Checkpoint interval, market maintenance interval and resource check interval. |

Changes affect future operations. Balances, installed jobs and paid costs, quoted obligations, historical transactions and saved deadlines are not rewritten. Existing grants are not replayed. Testing NPC market orders refresh under the selected policy on subsequent market operations; personal orders and escrow keep their saved terms. Disabling a population prevents new spawns without deleting residents or changing their mechanics.

## Overrides and compatibility

Supported overrides change the server-supplied **adjusted price** for an existing catalog item or the **manufacturing index** for an existing system. The client obtains these values through `config.GetAverageMarketPrices` and the industry facility response. IDs must exist in the installed catalog, prices must be positive, indices must be within zero and one, and entries cannot repeat. Enabling service costs requires adjusted prices for catalog materials with positive source base prices.

These overrides are economic projections belonging to the saved world. They do not modify the original item payload, base price, recipe or shared catalog cache. Two worlds using the same catalog retain separate active and pending definitions. Native-client physics, volumes, skills, recipes, identities and schema fields remain read-only. Adding another writable field requires an explicit compatible server/client synchronization contract.

Each document carries format version 1 and the installed catalog source fingerprint. Unknown properties, incompatible catalogs and invalid settings are rejected. Decimal economic inputs retain their precision in saved documents and existing SQLite projections. Original catalog files are never written.

## Local commands

Stop the server before these commands. They acquire the saved world's ownership file; a running server must be edited through the forthcoming authenticated local editor. Commands can upgrade an older world but do not activate pending revisions. Run from the repository root, supplying the saved world explicitly:

```powershell
dotnet run --project src/Eden.Server -- --world 'C:\Eden\world.sqlite' --configuration-history
dotnet run --project src/Eden.Server -- --world 'C:\Eden\world.sqlite' --configuration-export 1 --output 'C:\Eden\revision.json'
dotnet run --project src/Eden.Server -- --world 'C:\Eden\world.sqlite' --configuration-import 'C:\Eden\revision.json'
dotnet run --project src/Eden.Server -- --world 'C:\Eden\world.sqlite' --configuration-restore 1
```

History shows the active/pending IDs and recent revisions. Import and restore always create a new pending revision; start the server normally to activate it. A configuration save runs under the existing world owner and transactions, then requires the existing checkpoint mechanism to complete before reporting a durable save. A failed checkpoint retains committed pending state for retry and reports the failure; it does not activate the draft.

## Constant classification

Catalog facts stay in generated models/shared definitions. Native service IDs, effect operations, tax formulas, eligibility rules and mathematical invariants remain named code values. The table above lists server policy knobs. Settings never enable unsupported items or alter the build-specific support policy. Existing economic tables remain the transaction-owned projections used by features, while immutable revision documents own administration history.
