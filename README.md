# matOS

A self-hosted, browser-based **operating system for Docker** — an iOS/umbrelOS-style desktop
where every container appears as an app icon and opens in a draggable window. matOS is the
control-plane sibling to **[Matcad](https://github.com/Real-TTX/Matcad)** (the Caddy/xcaddy
reverse-proxy manager): matOS manages *apps* (containers, the App Store, the desktop UX) while
Matcad manages *routing* (it turns `matcad.*` container labels into Caddy config).

> **Milestone 1 (walking skeleton)** — deployable full stack on port **4333**, desktop shell +
> window manager, local login + roles + restart-persistent sessions, JSON config store, and
> reading existing Docker containers as app icons. The App Store, template/port-pool install
> engine and update monitoring are designed-for and land in later milestones.

## Stack

- **.NET 10 · ASP.NET Core Razor Pages** (matching the sibling projects), TagHelper control
  library, vanilla-JS window manager, SSE for live logs/stats.
- **Docker.DotNet** talks to the mounted engine socket.
- **JSON** is the primary store (configs, users, sessions) on a mounted data volume.
- Full stack in one compose: `matos` + `caddy` (matcad-caddy) + `matcad`.

## Quick start

```bash
# rebuild + redeploy the whole stack (Windows / PowerShell)
pwsh scripts/deploy.ps1

# or with docker compose directly
docker compose up -d --build
```

Then open **http://localhost:4333** and complete the first-run setup (create the admin account).

- matOS UI: `http://localhost:4333`
- Caddy (Matcad) serves app subdomains on `:80` / `:443`

The `caddy` and `matcad` services pull prebuilt images from GHCR
(`ghcr.io/real-ttx/matcad-caddy`, `ghcr.io/real-ttx/matcad`). If those packages are private,
run `docker login ghcr.io` first. To run **matOS on its own** (no proxy layer):

```bash
docker compose up -d matos
```

## How apps are reached (reverse proxy)

matOS never talks to Caddy directly. When it creates an app container it stamps Matcad's labels —
`matcad.enable=true`, `matcad.host=<slug>-<instance>.<BaseDomain>`, `matcad.port=<internal port>` —
and attaches the container to the shared `matos` Docker network. Matcad discovers the labels and
configures Caddy, so each install gets its **own subdomain** (this is how the same app can be
installed multiple times without host-port collisions). Set the base domain in **Settings**;
`apps.localhost` works locally with no DNS setup (`*.localhost` resolves to loopback).

> Enable "Docker discovery" once in Matcad's settings and set its base domain so it picks up
> matOS-labelled containers.

## Users, roles & sessions

- Local login with a simple role system (**Admin**, **User**); Entra/AD login is planned.
- Passwords are BCrypt-hashed; sessions are opaque GUID tokens stored as JSON on the volume, so
  **they survive a container restart**. DataProtection keys are persisted to the volume too.

## Configuration & data

Everything persists under the `matos-data` volume (`/app/data`):

- `config/*.json` — users, sessions, desktop + system settings
- `keys/` — DataProtection key ring
- `backups/` — rolling config backups

| Env var | Default | Purpose |
| --- | --- | --- |
| `MATOS_DATA_DIR` | `/app/data` | Data volume path |
| `MATOS_VERSION` | `local` | Version string (set by CI / deploy script) |
| `MatOS__Docker__Endpoint` | `unix:///var/run/docker.sock` | Docker engine endpoint |

## Local development

```bash
# fast inner loop (no container)
ASPNETCORE_URLS=http://localhost:4333 dotnet run --project src/MatOS.Web
```

On Windows, point matOS at Docker Desktop's engine to see containers:
`MatOS__Docker__Endpoint=npipe://./pipe/docker_engine`.

## Versioning & CI

Scheme: `release` → `<major>.<minor>.<build>-<date>`, `dev` → `nightly-<build>-<date>`,
`local` → `local-<date>` (`version.json` holds `major.minor`). GitHub Actions publish to
`ghcr.io/real-ttx/matos` — `.github/workflows/dev.yml` (branch `dev` → `:nightly`) and
`release.yml` (branch `main` → `:latest`).

Branches: **main** (release) · **dev** (development).

## Roadmap

- **M2** — App Store: Git-hosted Compose templates with `$PORT-*`/`$VAR` placeholders, a port
  pool, auto subdomains + `matcad.*` labels, and multiple installs of the same app.
- **M3** — Update monitoring (compare installed image digests against the catalog/registry).
- **M4** — More system apps (files/volumes, network, logs), Entra login, notifications.
