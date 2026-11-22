# Wiping a world

[Documentation index](README.md) / [Administration and settings](world-configuration.md)

Sign in to administration and open **World**. Choose how to recreate the world.

- **Recreate with existing settings** uses the revision shown in the form, including saved settings pending restart.
- **Configure new settings** opens [World setup](world-configuration.md#first-start) on the next start. The previous active
  settings are available as a starting point, and you can paste a custom configuration draft.

Wiping a legacy Fresh Universe save opens setup for an Established Universe with the seeded market.

Type `WIPE WORLD` and select **Back up, wipe world and stop server**. The request page reports the backup directory.
Wait for **World wipe complete** in the server console before restarting. Account creation and sign-in reopen when the
replacement world is ready.

The wipe removes every character (including bots), inventory, wallet, corporation, alliance,
structure, sovereignty claim, market order, industry job, mission, redemption entitlement,
simulation deadline and other gameplay record. Launcher sessions, refresh tokens and login
tickets are discarded; players sign in again and create new characters. Named mods' saved
world state is cleared too.

Accounts retain their IDs, usernames, password hashes, roles, creation dates and clone grades.
Configuration history, configured prices and industry indices are retained in the wiped save. A new setup approval
becomes the active revision and supersedes any old pending changes. Administrator credentials, catalog data, imported market/traffic reference
snapshots, skill authoring, mod packages, mod selection and deployment identities remain.
Internal ID sequence high-water marks remain. Fresh NPC populations, resources and market
supply are generated according to the retained settings on startup or when their systems load.
Unsaved settings in an open editor are not saved by the wipe.

The host first stops all listeners, background producers and mods, then saves the final resident
checkpoint. Only a successful shutdown reaches the wipe. The operation acquires the same
exclusive `.owner` file used by the running server and refuses a world that is still owned.
It copies the final checkpoint into a detached image and writes, flushes and checks the full
backup before clearing any rows. It also backs up the mod state database when present.
Deletion runs only in detached images. Account and configuration tables are retained. For **Configure new settings** and legacy
Fresh Universe wipes, the replacement checkpoint records that setup is required. The backup retains its original startup state.
The replacement world is checked for integrity and foreign key violations before the existing
atomic checkpoint writer installs it. A backup failure leaves the original saves intact.
If installing the world fails after installing cleared mod state, the original mod state is restored.

Backups are unique directories under `<world-path>.backups/<UTC timestamp>-<unique ID>/`:

- `world.sqlite`: the complete pre-wipe world, including characters and accounts.
- `world.sqlite.mods.sqlite`: the complete pre-wipe mod database, if present.
- `restore.txt`: destination paths and restore instructions.

Treat backups as private server data: they contain account password hashes. Wipes never
overwrite an existing backup directory or automatically prune these backups. Keep enough
disk space for the full backup and checkpoint replacement. The wipe leaves Eden stopped;
disable any external automatic restart policy while performing maintenance.

To restore, stop Eden and copy the backed-up world and mod database over the destinations
listed in `restore.txt`. Keep the matching catalog and mod packages. If the process or machine
is interrupted during replacement, restore both files before restarting; the pair of files is
not one atomic filesystem operation. Do not use the rotating `.previous` checkpoint as the
mandatory backup: later saves may replace it.

Automated coverage checks the final resident position in the backup, removal of gameplay
rows, retained account/password/configuration state, restart and sign-in, invalidated sessions,
mod state reset and recovery, failed backups, owned-world rejection, exact confirmation,
administrator authorization and antiforgery validation. Live acceptance is not performed;
agent client testing is disabled.

## Validation — November 17, 2026

**Automated validation:** The exact development filter was
`FullyQualifiedName=Eden.Protocol.Tests.Administration.WorldWipeTests.WipeBacksUpFinalResidentStateAndRetainsAccountsAcrossRestart`.
It passed after correcting the backup assertion to read the saved position without requiring a catalog
inside the backup directory. The first combined run exposed a Windows path-normalization error in
the disk-failure injector; normalizing its target exercised both partial-backup failure and replacement
rollback successfully. The subsequent development run passed all 24 selected cases.

Final validation restored the pinned tool and solution, built all projects with zero warnings/errors
(11.38 seconds), then ran the filter below: 75 passed, zero failed, one skipped (11 seconds of tests).
The existing browser interaction test requires opt-in `EDEN_TEST_ADMIN_BROWSER=1` and was skipped.
Catalog policy passed 8 cases (1.07 seconds); formatting policy passed 9 cases (0.02 seconds).
`tools/Format.ps1 -Check` and `git diff --check` passed. The full .NET suite was not requested or run.

```powershell
$filter = @(
  'FullyQualifiedName~Eden.Protocol.Tests.Administration.WorldWipeTests'
  'FullyQualifiedName~Eden.Protocol.Tests.Administration.AdministrationTests'
  'FullyQualifiedName~Eden.Protocol.Tests.Hosting.ServerLifetimeTests'
  'FullyQualifiedName~Eden.Protocol.Tests.Launcher.LauncherAccountTests'
  'FullyQualifiedName~Eden.Protocol.Tests.Persistence.CharacterCreationTests'
  'FullyQualifiedName~Eden.Protocol.Tests.Persistence.WorldInitializationTests'
  'FullyQualifiedName~Eden.Protocol.Tests.Persistence.LiveWorldTests'
) -join '|'
dotnet test --no-build --artifacts-path artifacts/feature-tests --filter $filter
```

**Live acceptance:** Not performed; agent client testing is disabled. Browser appearance remains unverified.

**Optional maintainer checks:** None required; backup contents, restart and the HTML form submission are covered automatically.
