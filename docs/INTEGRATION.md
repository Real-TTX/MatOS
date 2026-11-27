# Working with the other Mat projects (the engines)

MatOS sits on top of **MatDock**, **Matcad** and **Matddns** and talks to their REST APIs ([ENGINES.md](ENGINES.md)). Those APIs did not exist
(MatDock, Matddns) or lacked a few things (Matcad), so they were added **on a local branch `mattiverse-integration` in each repository**.
**Nothing was pushed or merged**: the branches are for you to review.

Built and verified on 2026-10-10 against real containers (images built from the branches as `matdock:mattiverse`, `matcad:mattiverse`, `matddns:mattiverse`).

## What the branches contain

| Repository | Branch | Added |
|---|---|---|
| **MatDock** | `mattiverse-integration` (13 commits) | `/api/v1` with `X-Api-Key` (`MatDock__Api__Key`): environments (ensure *local*), stacks (list, get, upsert, start/stop/restart/pull/redeploy/update, delete, logs), asynchronous operations with live output, bundle backups (back up, list, restore, delete, download as `.tar`) and bundle schedules with retention. `ManagedBy` on stacks and the badge *Managed by Mattiverse* (other values are shown as they are: *Managed by matos*). Local stacks keep compose and `.env` below the data volume. The `.env` is part of a bundle. CI also pushes `latest`; README documents the API and `MatDock__Admin__Seed`. 81 new tests. |
| **Matcad** | `mattiverse-integration` (4 commits) | `MATCAD_ADMIN_PASSWORD` (admin without the forced change, wizard done), `setupCompleted` in the settings API, anonymous `/healthz`, `app`/`version`/`apiVersion` in `/api/v1/status`, `managedBy` on routes, providers and authentications with the badge. README section *Provisioning (for Mattiverse)*. |
| **Matddns** | `mattiverse-integration` (4 commits) | A management API under `/api/v1` with `MATDDNS_API_KEY`: `sources`, `domains` (provider accounts with their records), `rules`, `run`, all as idempotent upserts, atomic and without a restart. `managedBy` with the badge, credentials never returned. `scripts/api-test.sh` (83 checks). README section *Management API (for Mattiverse)*. |
| **MatSMTP** | `feature/engine` (2026-10-19, built at the same time as MatOS's side) | The engine API under `/api/v1` with `MATSMTP_API_KEY`: status (with the SMTP port and TLS mode for the apps), relay users as idempotent upserts with `managedBy`, settings (`setupCompleted`, `defaultSenderDomain`); `MATSMTP_ADMIN_PASSWORD` seeds the administrator. [ENGINES.md](ENGINES.md) "MatSMTP" is the contract MatOS is built against. Until an image (`nightly` from `dev`, `latest` from `main`) carries it, the apps keep sending their mail themselves. |

Each branch keeps working without MatOS: nothing changes unless the new environment variables are set. Where the behaviour differs from the first sketch of the
contract, [ENGINES.md](ENGINES.md) has been updated to what was built (schedule times are UTC, a record that a rule uses cannot be dropped, ...).

## To release it

1. Review and merge the three branches (`git -C <repo> log mattiverse-integration`), then publish the images as usual.
2. The catalog entries use `ghcr.io/real-ttx/matdock:release`, `matcad:latest` and `matddns:latest`. **Until those images carry the new APIs, MatOS falls back
   to its own `docker compose` runner for everything** (nothing breaks, but nothing shows up in the engines either); the *Engines* tab says "API not answering".
3. MatDock now also publishes `:latest` for release builds, so the catalog can move from `:release` to `:latest` if you prefer.

## Found while building it, not fixed

**MatDock**
- `docker-compose.yml` in the repository sets the admin username and password without `MatDock__Admin__Seed` (the README had the same mistake and was corrected).
- Relative bind mounts (`./x`) in a stack of the local environment point into the MatDock container, not at the host (also true before). Catalog entries use named volumes for that reason.
- A restore into a deleted stack makes compose warn that the volume was not created by Compose (it works). A backup with stopped containers stops them volume by volume.
- Deleting a single volume archive in the History can break the bundle it belongs to; restoring over a Git-based stack turns it into an inline stack; deleting a stack leaves
  its files in the stacks folder; a bundle interrupted by a restart stays *running* until it is deleted (possible after 6 hours).

**Matcad**
- If Caddy cannot be reached, a route `POST` still answers 200 and stores the route but reports `applied.ok: false` after about 6 seconds.
- `Matcad:Caddy:AdminUrl` is effectively ignored (the default `http://caddy:2019` wins), so the Caddy service must be reachable as `caddy` (the catalog entry names it so).
- A `POST` without `id` always creates a route (no upsert by host): MatOS looks the id up first.
- `GET /providers` returns credentials in clear text; the image has no `HEALTHCHECK` and no `curl`; a browser with an admin cookie calling `/api/...` while the wizard is open is redirected to `/Setup`.

**Matddns**
- A failing rule retries every 30 seconds and logs an error each time (about 120 lines an hour per rule); without an `appsettings.json` every request, including the health checks, is logged at *Info*.
- The UI adds a `*` record with the zone as its host name where the API stores the real `*.zone`; `SaveInternal` skips saving silently when an unreadable config could not be backed up; without `domain` on a record the zone is
  guessed as the last two labels (wrong for `example.co.uk`; MatOS always sends it).
- Dynamic (pattern) rules are not part of the API. There is no `arm64` image yet.

**MatIdentity** (no branch of its own for MatOS: its changes for MatOS went to its `dev`)
- Its account pages (`/Account/Profile`, `/Account/Security`) are not branded: a person of MatOS who opens *Set up or change the second step* or *Change your sign-in
  password* from MatOS's account sees MatIdentity's own shell and name. Its branded sign-in covers the pages of a sign-in only (`SignInBranding.Pages`); a branded
  variant of the account layout (without its menu, the tabs and links of MatIdentity) would be needed. Not changed: no branch is named for it here, and it is more than a
  small change. Decide whether it should be done (docs/ARCHITECTURE.md "The second step").
- Nothing tells a broker whether a person's second step is on (SCIM and `/api/v1` have no field for it), so MatOS's tab *Security* cannot show it.

## What MatOS does about the engines

The engine layer, its fallbacks and the clean-up MatOS does are in [ARCHITECTURE.md](ARCHITECTURE.md). In short: it only writes objects marked `managedBy: matos`, only deletes what it created,
reads status and logs from Docker, and keeps a user's change to a stack in MatDock when it updates.

## Other suggestions

### All apps that should open inside MatOS

MatOS shows one app at a time inside its own frame. These send `X-Frame-Options: SAMEORIGIN` or a CSP
`frame-ancestors` and are therefore offered with *Open in a new tab* instead: **Matcad, Matddns, MatDock, Matmon, MatSplit**.
MatFile, MatPlay and the nginx test app embed fine. An option on each app such as
`<APP>__AllowEmbeddingFrom=https://matos.example.com` (sets `frame-ancestors` to that origin only) would let them run in
the frame without opening them to everyone. (Matcad's route option *Allow embedding* removes the header for the public route,
which is wider than needed; MatOS leaves it off.)

### Conventions that would make the catalog simpler

- **`<APP>_ADMIN_PASSWORD`** on every app (MatFile, MatMail, MatTrakr, Matddns and now Matcad have it; MatPlay, MatSplit, MatCMS, MatBu
  start as `admin`/`admin`; MatPaper, MatDo and Matgate use a first-visit wizard).
- **`/healthz`** returning `200` anonymously (MatPaper, MatPlay, MatCMS, MatDo, MatTrakr have none).
- **An `x-matos` block in each project's own compose file**, so the catalog entry comes from the app's repo and cannot
  drift from it ([CATALOG.md](CATALOG.md)).
- **MatPaper**: the database password is the fixed `matpaper`; an environment variable for it would allow a generated one.

Not in the catalog: **msBi** (no anonymously pullable image), **Matgate** (four containers plus a browser farm),
**Matstat** (local build and MSSQL), **Matcat** (the old name of Matcad).

## Other findings

- **Port 8030**: the first choice, 8020, is the default of the *Schulio* project (`Schulio/docker-compose.yml`, `SCHULIO_PORT=8020`),
  which was running on the development PC. 8030 is used by no project in the development folder and was free on that PC.
  `MATOS_PORT` still changes the host port if it is ever taken.
- `Prompt-Docker-Init-Mattiverse.txt` and `Prompt-Docker-Init-MatDock.txt` in the development folder are verbatim copies of the
  MatSplit prompt (port 4774, "Projektname: MatSplit"). MatDock really uses 4455.
- Host ports used by more than one project (found while collecting the catalog): `9101` (MatCMS and Feusys.Web),
  `9262` (TabSale and TapSale), `4433` (Matcad and the old Matcat), `80`/`443` (Matcad and the Caddy inside the stack of the older matOS desktop).
  MatOS picks a free port from its pool when the preferred one is taken and warns about the fixed ones.
- **The older matOS desktop** (repository Real-TTX/MatOS-legacy, archived) and this app overlap in purpose (see [ARCHITECTURE.md](ARCHITECTURE.md)). Since the rename
  they share their Docker names and their image: both are the compose project `matos` with the service and container `matos`, the volume `matos_matos-data`
  and the shared network `matos`, and both name `ghcr.io/real-ttx/matos` (the older desktop's workflows publish it). They **cannot run side by side** on
  one host any more. **Decided (the owner, 2026-11-26): the older desktop is retired**: repository `Real-TTX/MatOS-legacy` (archived), images
  `ghcr.io/real-ttx/matos-legacy`; this app publishes `ghcr.io/real-ttx/matos` (and for a while also `ghcr.io/real-ttx/mattiverse`, `LEGACY_IMAGE_NAME` in `build.yml`).
