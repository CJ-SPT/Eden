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

Use `docker compose stop` before backing up data or running commands that open the saved world. Docker sends SIGTERM and gives Eden two minutes to drain connections and finish its checkpoint before enforcing the stop. See the [Compose stop settings](https://docs.docker.com/reference/compose-file/services/#stop_grace_period).

For example, to set the existing Eden account's launcher password with the service stopped:

```sh
docker compose run --rm eden --config /config/server.json --set-account-password Eden
docker compose up -d
```

The administration interface remains bound to the container's loopback address. Windows development tickets and compiling client packages from the Windows client runtime require a native Windows host. The container can load and serve existing compatible packages from the mounted data directory.
