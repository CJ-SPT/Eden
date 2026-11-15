# Docker deployment

[Documentation index](README.md) / [Launcher hosting](launcher.md)

Eden runs as a Linux container under Docker Engine or Docker Desktop in Linux-container mode. Compose mounts the server data directory at `/data` and the hosting configuration at `/config`. The world, previous checkpoint, catalog, chat certificate and installed mods persist on the host across image rebuilds and container replacement.

## Prepare the data and TLS configuration

1. Generate the pinned `catalog.sqlite` on Windows using the [catalog setup guide](development-client.md#creating-and-recreating-the-databases). By default, Compose uses `src/Eden.Server/data` from this checkout. On another machine, copy the catalog there, or set `EDEN_HOST_DATA_DIR` to the directory that contains it. Eden creates `world.sqlite` at first startup if the directory contains only a catalog.
2. To continue an existing world, stop its current host before copying the data directory. Include `world.sqlite`, `catalog.sqlite`, and the associated files listed in the [Windows migration guide](development-client.md#moving-an-existing-server). Run one Eden instance per data directory. On Linux, give the container user (UID/GID 1654) write access to the data directory and world files and read access to the catalog, configuration and TLS certificate.
3. Create `deploy` in the checkout. Copy `server.docker.example.json` to `deploy/server.json` and place your PKCS#12 certificate at `deploy/server.pfx`. Change `GameAddress` and `GatewayAddress` to the host name players will use. The certificate must cover that name. Set `EDEN_CERTIFICATE_PASSWORD` in the shell or a local Compose `.env` file for a password-protected PFX. Set `EDEN_HOST_CONFIG_DIR` if your configuration lives elsewhere.

Compose publishes TCP 26000 (game), 26001 (HTTPS gateway), 26002 (HTTPS launcher API) and 5222 (native chat). It publishes to host loopback by default. For remote players, set `EDEN_BIND_IP` to the intended host address and allow the four TCP ports through the host firewall. Keep `ListenAddress` at `0.0.0.0` inside the container. If you change server ports, update the Compose mappings and advertised addresses together. See the [Docker port publishing guide](https://docs.docker.com/get-started/docker-concepts/running-containers/publishing-ports/).

## Start and stop

From the repository root, run:

```sh
docker compose config --quiet
docker compose up --build -d
docker compose logs -f eden
```

Once Eden logs that its listeners are ready, use `https://eden.example.com:26002` in the launcher with the host name from your certificate. For a private CA, configure launcher trust using the [remote hosting guide](launcher.md#remote-hosting). Set `EDEN_REVISION` to the Git commit SHA before building to include it in the startup banner.

To offer CA downloads, place the public CA certificate at `deploy/ca.cer` and add `"CertificateAuthorityPath": "/config/ca.cer"`
to `deploy/server.json`. After restarting Eden, use `docker compose logs eden` to get the CA fingerprint to share with players.
See [CA certificate downloads](launcher.md#ca-certificate-downloads).

Use `docker compose stop` before backing up data or running commands that open the saved world. Docker sends SIGTERM and gives Eden two minutes to drain connections and finish its checkpoint before enforcing the stop. See the [Compose stop settings](https://docs.docker.com/reference/compose-file/services/#stop_grace_period).

For example, to set the existing Eden account's launcher password with the service stopped:

```sh
docker compose run --rm eden --config /config/server.json --set-account-password Eden
docker compose up -d
```

Windows development tickets still require a Windows host. The container builds mod sets from a verified original client
archive and publishes them to remote players through Eden Launcher.

### Build caching on a remote host

Use a current Docker Engine with BuildKit and Compose v2. The Dockerfile restores NuGet dependencies before copying
source files and shares a NuGet package cache between restore and publish. Ordinary source updates can reuse the
restore layer; project changes can reuse already downloaded packages. Python 3 installation and the standalone
Python 2 compiler are independent of server binaries, so a source update does not reinstall or rebuild them.
This follows [Docker's cache guidance](https://docs.docker.com/build/cache/optimize/).

Keep using the same builder on the remote host. Avoid `--no-cache` and routine `docker builder prune` or
`docker system prune` calls when retaining build speed matters. A cold builder still downloads dependencies and
compiles Python 2 once. The first build after the Dockerfile changes also needs to populate the new cache layers;
subsequent builds benefit from them. Server source changes still require a Release compilation.

To capture step timings without stopping the running server:

```sh
docker compose --progress plain build eden
```

After the cache is warm, source-only updates should show `CACHED` for NuGet restore, Python 3 installation and
Python 2 compilation. To measure an unchanged build, repeat that command with the same `EDEN_REVISION` value.
Changing `EDEN_REVISION` intentionally republishes the server so its startup banner records the correct commit.
Once the image build succeeds, deploy it with `docker compose up -d eden`, including any Compose overrides used
by the running service. Include the same overrides when collecting timings.

#### Automated validation

On November 15, 2026, tool and solution restore passed, the solution built with zero warnings and errors in 13.82 seconds,
and a separate Release restore followed by `publish --no-restore` succeeded with `UseAppHost=false`. The exact initial
filter was `FullyQualifiedName=Eden.Protocol.Tests.Hosting.ServerDataPathsTests.SourceAndPublishedServersUseTheirOwnDataDirectory`;
it passed one test. The combined filter selected `ServerDataPathsTests` and `GameplayDependenciesTests`; all 12 tests
passed in a 5.62-second test command. Both Python policy suites passed (8 catalog and 9 formatting tests),
and `tools/Format.ps1 -Check` passed.
Docker was unavailable in the workspace, so Linux image construction, cache hits and remote build speed remain unmeasured.

#### Live acceptance

Not performed; agent client testing is disabled.

#### Optional maintainer checks

Run the timing command on the Docker host for a source update after warming the cache and compare its publish time
with the previous build. Build the replacement image while Eden is running, then recreate the service after it succeeds.

## Enable AutoMining on a Docker host

Copy the original **build 3662168** archive to `./deploy/.eden-original-code.ccp` on the Docker host. If you use
`EDEN_HOST_CONFIG_DIR`, copy it there instead. Use `.eden-original-code.ccp` from a prepared client, or rename
`tq/code.ccp` from an unchanged client. Compose already mounts this directory read-only at `/config`. The compiler
needs only the archive, so the rest of the Windows client can stay on the player’s PC.

Run these commands in the deployment checkout after updating it to a revision with Docker mod compilation.

```sh
docker compose build eden
docker compose stop eden
docker compose run --rm -T eden --mod-export-automining /data/AutoMining-1.2.3.edenmod
docker compose run --rm -T eden --mod-install /data/AutoMining-1.2.3.edenmod
docker compose run --rm -T eden --mod-enable NEW_PACKAGE_SHA256
docker compose run --rm -T eden --mod-build /config
docker compose up -d eden
docker compose run --rm -T eden --mod-status
```

Use a fresh output filename when exporting again. Replace `NEW_PACKAGE_SHA256` with the hash printed by install.
When upgrading, run `docker compose run --rm -T eden --mod-disable OLD_PACKAGE_SHA256` before enabling the new hash.
Use the same Compose files and environment as your running deployment. If the service uses a nondefault world path,
append the same container-side `--world PATH` to each mod command. Status should list `eden.automining` version
`1.2.3` in the active set after startup. Then connect to the server’s existing URL in Eden Launcher, let it download
the required mod set, and open **Eden Mods → AutoMining** in the Neocom. See [in-game controls](modding.md#automining).

The image includes Python 3 and a standalone CPython 2.7.18 compiler. It reads the pinned archive as data and
writes the composed mod set to the mounted server data directory. Supply the original archive separately.

### Automated validation

The .NET build passed in 4.66 seconds. The 40 focused tests passed in 12.20 seconds with standalone CPython 2.7.18
on Windows. The launcher API test rejected a mismatched archive, built AutoMining from the original archive alone,
downloaded the active set over the authenticated API, and received a launch ticket for its exact archive hash.
The same launcher test passed with the pinned Windows DLL. The native adapter, Python policy suites, and formatting
checks passed. The focused filter selected `AutoMiningPackageTests`, `ModLauncherApiTests`, `ModRuntimeTests`,
`ModFoundationTests`, `SkillEditorTests`, and `GameplayDependenciesTests`. Docker was unavailable in the test
workspace, so the Linux image build and Compose commands were unverified.

### Live acceptance

Not performed. Agent client testing is disabled.

### Optional maintainer check

Build the image and activate the set on the Docker host, then open the AutoMining panel using Eden Launcher.

## Remote administration

Include `compose.admin.yaml` to publish the administration page over HTTPS on TCP 26003. Add these values to the deployment's `.env`, using your Docker host's LAN or VPN address and the hostname covered by `CertificatePath` in `deploy/server.json`.

```dotenv
EDEN_ADMIN_BIND_IP=192.168.1.10
EDEN_ADMIN_ORIGIN=https://eden.example.com:26003
```

The browser must trust the deployment certificate and resolve the hostname to the published address. Permit TCP 26003 from the networks that need administrator access. `EDEN_ADMIN_BIND_IP` controls only the admin port. `EDEN_ADMIN_ORIGIN` must match the scheme, hostname and port that administrators enter in the browser.

If the world already has an administrator, use the existing credentials. Otherwise provision one while the service is stopped. The command prompts for the password and confirmation.

```sh
docker compose stop eden
docker compose run --build --rm eden --config /config/server.json --set-admin-password Corey
```

Build and recreate the service with both Compose files, then open `EDEN_ADMIN_ORIGIN` on the remote machine.

```sh
docker compose -f compose.yaml -f compose.admin.yaml config --quiet
docker compose -f compose.yaml -f compose.admin.yaml up --build -d
```

Include both files when recreating the service. The base Compose file by itself publishes only the game, launcher, gateway and chat ports. [Docker Compose file merging](https://docs.docker.com/compose/how-tos/multiple-compose-files/merge/)
