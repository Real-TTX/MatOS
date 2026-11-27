# Working with the other Mat projects (the engines)

MatOS sits on top of **MatDock**, **Matcad** and **Matddns** and talks to their REST APIs ([ENGINES.md](ENGINES.md)). Those APIs did not exist
(MatDock, Matddns) or lacked a few things (Matcad), so they were added **on a local branch `mattiverse-integration` in each repository**.
**Nothing was pushed or merged**: the branches are for you to review.

Built and verified on 2026-10-10 against real containers (images built from the branches as `matdock:mattiverse`, `matcad:mattiverse`, `matddns:mattiverse`).

## What the branches contain

| Repository | Branch | Added |
|---|---|---|
| **MatDock** | `mattiverse-integration` (13 commits) | `/api/v1` with `X-Api-Key` (`MatDock__Api__Key`): environments (ensure *local*), stacks (list, get, upsert, start/stop/restart/pull/redeploy/update, delete, logs), asynchronous operations with live output, bundle backups (back up, list, restore, delete, download as `.tar`) and bundle schedules with retention. `ManagedBy` on stacks and the badge *Managed by Mattiverse* (other values are shown as they are: *Managed by matos*). Local stacks keep compose and `.env` below the data volume. The `.env` is part of a bundle. CI also pushes `latest`; README documents the API and `MatDock__Admin__Seed`. 81 new tests. |
| **MatDock** | `feature/backup-targets-api` (2026-11-27, from `dev`) | Backup targets over the API (`/api/v1/backup-targets`: list, get, create, change, delete, test saved and unsaved), a new target kind **SFTP** (SSH.NET, password or key, host key pinned on first use), `managedBy` on targets, a target with bundles or a schedule is not deleted; apiVersion 5. MatOS's *Backups, Targets* is built on it. |
| **MatDock** | `feature/nodes-api` (2026-11-27, from `dev`) | The nodes of MatOS (docs/ARCHITECTURE.md "Nodes"): `GET /environments` names `managedBy`, port, user, the last message and the public key of a generated key; `PUT /environments/ssh/{name}` takes `managedBy` (the badge in the list of environments, migration `AddEnvironmentManagedBy`); `DELETE /environments/{id}?managedBy=` (not the local one, not with stacks or schedules); a restore into another environment, `volumesOnly`; apiVersion 8. Tests, README. |
| **Matcad** | `mattiverse-integration` (4 commits) | `MATCAD_ADMIN_PASSWORD` (admin without the forced change, wizard done), `setupCompleted` in the settings API, anonymous `/healthz`, `app`/`version`/`apiVersion` in `/api/v1/status`, `managedBy` on routes, providers and authentications with the badge. README section *Provisioning (for Mattiverse)*. |
| **Matddns** | `mattiverse-integration` (4 commits) | A management API under `/api/v1` with `MATDDNS_API_KEY`: `sources`, `domains` (provider accounts with their records), `rules`, `run`, all as idempotent upserts, atomic and without a restart. `managedBy` with the badge, credentials never returned. `scripts/api-test.sh` (83 checks). README section *Management API (for Mattiverse)*. |
| **MatSMTP** | `feature/engine` (2026-10-19, built at the same time as MatOS's side) | The engine API under `/api/v1` with `MATSMTP_API_KEY`: status (with the SMTP port and TLS mode for the apps), relay users as idempotent upserts with `managedBy`, settings (`setupCompleted`, `defaultSenderDomain`); `MATSMTP_ADMIN_PASSWORD` seeds the administrator. [ENGINES.md](ENGINES.md) "MatSMTP" is the contract MatOS is built against. Until an image (`nightly` from `dev`, `latest` from `main`) carries it, the apps keep sending their mail themselves. |
| **MatDock** | `feature/host-updates-api` (2026-11-27, from `dev`) | The updates of the host's operating system over the API (`apiVersion` 7, after the backup targets (5) and the Task Manager (6)): `GET /api/v1/host/updates`, `POST …/check`, `POST …/apply` (every package, `apt-get upgrade --with-new-pkgs`, on the host's systemd), `GET …/runs/{unit}` (read from the host), `POST /api/v1/host/reboot` (`{confirm: "reboot"}`). Tests `HostUpdatesApiTests`, `HostUpgradeCommandTests`; its docs/UPDATE-MANAGEMENT.md section 18. Until an image carries it, Settings, Updates asks for an update of the engines. |

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
- **Changed on its `dev` (2026-11-27, not pushed): the account in MatOS's look.** `/Account/Profile` and `/Account/Security` opened with `?brand=<clientId>` (the links of
  MatOS's tabs Security and Password) are in the look of that branded client: the layout `_BrandedAccount` (its head `_BrandHead`, shared with `_SignIn`; a bar with
  *Back to MatOS*; no menu; with `hideIdentity` no word of MatIdentity, and the authenticator app names the app). Every tab, link, form and redirect keeps `brand`;
  only the link names it (`SignInBranding.AccountBrandAsync`), never the cookie of a sign-in, so MatIdentity's own account is unchanged. Tests: `BrandingTests`
  (the account opened from a link of the app), all 175 green. Its docs/ARCHITECTURE.md "Branded sign-in" and CLAUDE.md say so. Review and publish its image; until then
  the dev stack's MatIdentity (`ghcr.io/real-ttx/matidentity:latest`) ignores `brand` and shows its own shell (docs/ARCHITECTURE.md "The second step").
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

## The Mat apps in the store (surveyed 2026-11-27)

Every Mat app of the development folder with a published image, read on the branch its `:latest` image is built from (`main` everywhere; `dev` builds `:nightly`).
**Sign-in**: reads `MATIDENTITY_*` (the MatPlatform kit: callbacks `/signin-oidc`, `/signout-callback-oidc`, `/signout-backchannel`; its login page goes straight on to
MatIdentity unless `?local=1`). **Mail**: really sends mail through `MATSMTP_*` (every app on the kit reads them, only these send). **Window**: reads `MAT_FRAME_ANCESTORS`
(the entry names it, so MatOS writes no frame headers on its route); without it MatOS sets `frame-ancestors` on the route unless the app sends a CSP of its own.

| App | State | Sign-in | Mail | Window | `/api` | What is missing |
|---|---|---|---|---|---|---|
| MatBoard, MatDrive, MatOffice, MatShape | beta | ✅ | ✅ | ✅ | ✅ | an end-to-end test through MatOS |
| MatBu | beta | ✅ (`/Login`) | ✅ | ✅ | ✅ | the same |
| MatDo, MatCloud, MatPaper | beta | ✅ | – | ✅ | ✅ | the same (MatPaper's database password is fixed in its config) |
| MatPhoto, MatContactHub, MatDown, MatRegistry | beta (new) | ✅ | ✅ | ✅ | ✅ | the same; MatRegistry: `docker push` of big layers through Matcad is untested, remote pushes need HTTPS (a domain); MatContactHub publishes LDAP on 389/636 |
| MatGame | beta (new) | ✅ | – | ✅ | ✅ | pictures (its repository has none) |
| MatFile, MatPlay, MatSplit, MatTrakr, MatMail | beta | ✅ | – | route headers | ✅ (kit 1.4) | `MAT_FRAME_ANCESTORS` (kit 2.x); MatMail's note "not in the window" may be stale (it sends no CSP of its own) |
| MatNotes, MatBill, MatTicket, MatPhone, MatCan | alpha (new) | – | – (own settings) | – (`frame-ancestors 'self'`) | – | the MatPlatform kit: the one sign-in, `MAT_API_KEY` and `/api`, `MAT_FRAME_ANCESTORS`, a seeded administrator (today the first visitor becomes it); MatPhone publishes SIP 5060 and RTP 6100-6199/udp |
| MatCMS, Matmon | alpha | – | – (own settings) | – | – | the kit; their entries no longer claim `mail.smtp` (the images never read `MATSMTP_*`, the binding only made a relay user) |
| MatDesign | alpha | – | – | – | – | – |
| MatAi | preview | ✅ | – | – (own CSP) | ✅ | a remote and a published image |
| MatBiz | preview (new) | ✅ (in the code) | – | – | ✅ | `:latest` is still the old Matbiz (repository `Matbiz-old`) and there is no `:nightly`: push `main` of the new MatBiz, then make the entry a beta |

Not in the store: **MatHub** (its image is private), **MatCMS.Cloud** (the hosted side of MatCMS, not an app to install), **MatGame2**, **Matarchive**, **Matlan**, **Matstat** (no image).

Changes made to the entries with the survey: `ui.signIn` for MatPlay, MatSplit, MatTrakr, MatMail, MatPaper, MatBu and MatAi (MatFile opens its sign-in by itself);
`MAT_FRAME_ANCESTORS` named for the apps that read it; the icons of MatPaper, MatMail, MatBu and MatTrakr (an SVG now) taken from their repositories; pictures for every app
that has some. No app repository was changed.
