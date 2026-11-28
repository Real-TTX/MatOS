# The app catalog

An app in the store is a normal **`docker compose` file** with one extra top-level block, `x-matos`. Compose ignores
`x-*` keys, so the same file also runs with plain `docker compose up`.

Built-in entries live in [`src/MatOS.Core/Catalog/apps`](../src/MatOS.Core/Catalog/apps) and are embedded in the
image. Your own entries go to **`/data/catalog/*.yml`** (the data volume); use **Store → Reload catalog** to pick them up.
A file with the same `id` as a built-in entry **replaces** it, and a file that cannot be parsed is skipped and logged.

## Example

```yaml
x-matos:
  id: wiki                    # lowercase letters, digits, dashes; the folder and project name derive from it
  name: Wiki
  tagline: Notes for the family
  description: A longer text for the install page.
  category: Productivity      # groups the store
  role: app                   # app | proxy | ddns | docker | identity | mail  (the role of an engine: Matcad, Matddns, MatDock, MatIdentity, MatSMTP)
  icon: wiki.svg              # a file below wwwroot/img/apps/, an https URL, or leave out for a generated tile
  homepage: https://example.com/wiki
  screenshots:                # optional: the pictures of its page in the store (a file below wwwroot/img/store, or an https URL in an entry of your own)
    - src: wiki/home.webp
      caption: The start page   # what it shows, in a few words: its alt text and the line under it
  recommended: false          # offered first in the store (infrastructure roles only)
  core: false                 # true for the five engines: installed by the wizard, cannot be uninstalled
  state: beta                 # integrated | beta | alpha | preview | third-party: how far the app is along in MatOS (see "App states")
  stateNote: Not yet tested end to end through MatOS.   # optional, one sentence: what is missing for the next state
  ui:
    service: wiki             # the compose service with the web UI
    port: 3000                # its port inside the container
    hostPort: 3000            # tried first on the host; a free port from the pool is used when taken
    path: /login              # optional: appended when the app is opened
    signIn: /Account/Login    # optional: where its sign-in starts; with MatIdentity the app is opened here, so the person lands in it without a click
  shared: [proxy]             # more services that must be reachable by the reverse proxy (the UI service always is)
  routes:                     # only for the reverse proxy (a provider of web.route): the service that listens for the routes;
    service: caddy            # without a domain MatOS publishes the port of every app here (the apps publish none)
  provision: matcad           # optional: a built-in provisioner to run after the install (matdock | matcad | matddns | matidentity | matsmtp)
  platform: true              # optional: the app speaks the platform contract (MatDesign docs/PLATFORM.md): MatOS gives it MAT_API_KEY and a Manage page
  consumes:                   # optional: what the app uses of other apps (MatDesign docs/PLATFORM.md "Capabilities"); the broker binds each when a provider is installed
    - capability: identity.oidc   # the sign-in through MatIdentity (params: callback /signin-oidc, signedOut /signout-callback-oidc, backchannel /signout-backchannel)
    - capability: mail.smtp       # its mail through MatSMTP (params: sender = the part before the @, default the slug)
  notes:
    - Shown as a hint on the install page.
  variables:
    - key: ADMIN_PASSWORD
      label: Admin password
      type: secret            # text | secret | number
      generate: true          # an empty answer becomes a random password
      help: Leave empty to generate one.
    - key: DB_PASSWORD
      type: secret
      generate: true
      hidden: true            # not asked: always generated
    - key: TIMEZONE
      default: Europe/Berlin
      advanced: true          # below "Advanced" in the form
  folders:                    # optional: data folders the person may put on a volume of their own (see "Data folders")
    - volume: wiki-files      # a named volume of the compose file (top-level volumes) that a service mounts
      name: Attachments       # what it holds, in a few words
      purpose: The files people attach to pages.   # optional, one sentence: the hint of the choice
      readOnly: false         # true: the app only reads it, a chosen volume is mounted read-only
  credentials:                # the "Initial credentials" card of the app (admins only)
    - label: Username
      value: admin
    - label: Password
      value: ${ADMIN_PASSWORD}
      secret: true            # hidden until the admin asks to see it

services:
  wiki:
    image: ghcr.io/example/wiki:latest
    restart: unless-stopped
    environment:
      ADMIN_PASSWORD: ${ADMIN_PASSWORD}
      TZ: ${TIMEZONE:-UTC}
    volumes:
      - wiki-data:/data
      - wiki-files:/data/files

volumes:
  wiki-data:
  wiki-files:
```

## Rules

- **Do not publish the UI port in the compose file.** MatOS publishes it itself (`ui.port` on a free host port) in the
  rendered compose file. Ports that are fixed on purpose (80/443 of a proxy, the mail ports of MatMail) are written in the
  compose file; MatOS checks them before the install and warns when another container holds them.
- **Named volumes only.** A relative bind mount would resolve inside the MatOS container.
- **Answers reach compose as `${KEY}`** from the app's `.env`. Besides your variables you can use `${MV_SLUG}`,
  `${MV_PROJECT}`, `${MV_PUBLIC_HOST}` and `${TZ}`. Values must not contain a single quote or a line break.
- Every `${KEY}` in the compose file needs a variable or a default (`${KEY:-default}`); the unit tests check this for the
  built-in entries.
- A catalog entry with `provision: matcad` (or `matddns`, `matdock`, `matidentity`, `matsmtp`) connects the engine after the install; other provisioners are C# classes
  implementing `IAppProvisioner`. An engine also needs its API key as a generated, hidden variable (`MATCAD_API_KEY`, `MATDDNS_API_KEY`,
  `MATDOCK_API_KEY`, `MATSMTP_API_KEY`; MatIdentity uses `MAT_API_KEY`) that the compose file passes to the container the way the engine expects it (see [ENGINES.md](ENGINES.md)).
  The five engines are `core: true` (the wizard installs them, the app list cannot remove them); `EngineIds.All` names them in the order they are installed.

## Data folders

Some apps keep files the person wants elsewhere: the photos of a photo app on the NAS, the documents of a file app on a bigger disk. `folders` names the volumes of the
entry that may be put on a **volume of MatOS** (made under *Volumes*: on this machine, an SMB share or an NFS share; docs/ARCHITECTURE.md "Volumes and the file explorer"):

- `volume`: a key of the top-level `volumes` of the compose file, mounted by at least one service (short `wiki-files:/path` or long `{type: volume, source, target}`);
  not an `external` one. The parser reads where it is mounted (shown as the hint) and refuses a folder that names no such volume, has no `name` or is named twice.
- `name` and `purpose`: what the install form (*More options*) and the app's page *Data folders* show; `readOnly: true` for an app that only reads the folder (a chosen
  volume is mounted read-only: `:ro`, or `read_only: true` in the long form).
- **Without a choice nothing changes**: the volume stays the app's own, as compose makes it. With one, MatOS renders it as `external: true` with the name of the chosen
  volume; the rest of the entry stays as it is. The choice is kept as the variable `MATOS_FOLDER_<VOLUME>` (upper case, other characters `_`; empty = its own volume),
  which the entry neither declares nor uses.
- Keep the folder a volume of its own (not the data volume of the app with its database), so a share holds only what belongs there. MatDock leaves a network share out of
  the app's backups.
- In use: **MatPhoto** (`matphoto-photos` at `/storage/photos`, the *Photo library*, which MatPhoto reads as a local storage).

## The store

The plain first look of the store is an app store: the **app of the day** (one app of *Made for MatOS* with a picture, another one each day), the row **Made for MatOS**
(every `app` that is integrated or a beta and consumes `identity.oidc`) and a **shelf per category** (`category`; *System* last) with *See all*. A card shows the icon,
the `tagline`, the state when it is not yet a beta, and marks for `identity.oidc` (the one sign-in) and `mail.smtp` (mail through MatOS). The page of an app shows the
`screenshots` in a row that scrolls sideways, the `description`, what the app gets from MatOS, its facts and more apps of its category.

Pictures of a built-in entry are WebP files, at most 1280 px wide, in `src/MatOS.Web/wwwroot/img/store/<id>/` (taken from `docs/images` of the app's repository);
`CatalogTests` checks that every icon and picture is there and that every Mat app has one to six. Categories in use: Productivity, Files, Media, Communication,
Business, Finance, Developer, Fun, Monitoring, Tools, Websites, System.

## App states

Every app has a `state`; the store shows it as a pill and filters by it, and the README lists every built-in app with it (`AppStateTests` keeps the table in step
with the catalog: run the tests with `MATOS_WRITE_README=1` after a change). The values are `AppStates` in `CatalogModels.cs`:

| State | Meaning |
|---|---|
| `integrated` | fully part of MatOS and tested through it (the engines, an app that uses all of them) |
| `beta` | uses the engines (the one sign-in, ...), not yet tested end to end through MatOS |
| `alpha` | installs and runs, its integration is not released yet: the install page warns |
| `preview` | announced only (its image is not published): the store does not install it |
| `third-party` | not a Mat app: it runs as its maker built it; where it can, the entry connects it to the one sign-in and the mail (see "Apps of other makers") |

Every built-in entry names its state (a unit test checks); an entry in `/data/catalog` (or of Mattiverse) without one is `beta`. An unknown state is an error of the entry.
`stateNote` is one sentence on one line: what is missing for the next state (for an integrated app, what it does).

## The platform contract and what an app uses

- `platform: true` adds the hidden, generated variable `MAT_API_KEY`; the compose file passes it as `MAT_API_KEY: ${MAT_API_KEY}`. The app gets a tab **Manage**, drawn from its `/api`.
- `consumes` lists the capabilities the app uses (`capability`, `version` (default 1), `params`): `identity.oidc` for the sign-in through MatIdentity, `mail.smtp` for its mail
  through MatSMTP, or any other (MatDesign docs/PLATFORM.md "Capabilities"). The broker binds each to an installed provider and passes the binding's variables
  (`MATIDENTITY_*`, `MATSMTP_*`, ...) to the app's web service and its `.env`; without a provider they are empty and the app does it itself. An app whose image says what it
  consumes in its own `/api` needs no entry (what the `/api` says is added; it wins for the same capability). `identity.oidc` needs a `ui`.
  A capability another app provides works the same way: MatCMS (no `/api`) consumes `cms.cloud` in its entry, MatCMS Cloud (`platform: true`) provides it in its `/api`,
  and the broker binds every MatCMS to the cloud (`MAT_CMS_CLOUD_URL`, `_INTERNAL_URL`, `_KEY`, a key capability: docs/INTEGRATION.md "MatCMS Cloud"). The provider must be
  `platform: true` (the broker reads what it provides from its `/api` and binds with its `MAT_API_KEY`); the consumer need not.
- The old keys `identity: {}` (with `callback`, `signedOut`, `backchannel`) and `mail: {}` (with `sender`) are read as `identity.oidc` and `mail.smtp` with those parameters, so a
  catalog copy of an earlier MatOS (or of Mattiverse) keeps working. The variables an earlier MatOS filled for them are taken over as the app's bindings at its next deploy.
- A service other than the web service that needs the variables (the one that sends the mail) names them in the compose file (`MATSMTP_HOST: ${MATSMTP_HOST:-}`): the values are in the
  `.env`. The web service gets them by itself.

## Apps of other makers

A well-known app (Nextcloud, Vaultwarden, Paperless-ngx, ...) is an entry like any other, with `state: third-party`, its official image (the major tag where the
maker publishes one, else the line its own compose file follows), named volumes and its database as a service of the stack. It reads its settings under its own names,
so the entry translates; no code of MatOS knows the app:

- **The sign-in and the mail.** The entry `consumes` `identity.oidc` with the paths of the app (`callback`, `signedOut`; `backchannel: ""` when the app takes no
  back-channel logout) and `mail.smtp` with its `sender`, and passes `MATIDENTITY_*` and `MATSMTP_*` to the service. Its **start** (`entrypoint: [/bin/sh, -c, ...]`
  that ends with `exec` of the image's own entrypoint) sets the app's names only while a binding is there: an empty setting would stop most apps
  (`if [ -n "$$MATIDENTITY_CLIENT_ID" ]; then export SSO_ENABLED=true ...; fi`). `$$` is a `$` for the shell; a single `$` is filled by compose from the `.env`.
- **Its public address.** `MAT_PUBLIC_URL` (the route under the domain, or this host by the address of the settings) reaches an app outside the contract only when its
  compose file names `${MAT_PUBLIC_URL:-}`: Vaultwarden's `DOMAIN`, Paperless's `PAPERLESS_URL`, Nextcloud's trusted domain. Empty while MatOS does not know a real address.
- **Settings only a command sets** (Nextcloud's `occ`): a script as a top-level `configs:` entry with `content:` (compose writes it), mounted into the service and run by
  the app's own hook at every start, so it follows the bindings.
- **What a sign-in of another maker needs**: the apps ask MatIdentity at its public address, server to server (they know no internal address). That address has to be
  reachable from the containers: a domain whose name resolves to this machine from inside, or the address of this machine in the settings (not `localhost`). MatIdentity asks
  every app for PKCE; the entries switch it on where the app has a setting for it.

- **A first start that takes long** (Nextcloud installs itself for a minute or two): MatOS deploys the stack again a few seconds after the install, once the
  bindings are there, and that recreates the web service. `stop_grace_period` long enough for the first start lets it finish (the start script runs as process 1,
  which ignores the stop signal until the app's own server takes over).
- **What the test showed** (a second MatOS, 2026-11-27): MatIdentity names its issuer with a slash at the end, and before its dev of 2026-11-27 (0747e53) the binding's
  `MATIDENTITY_AUTHORITY` came without it, so an app that compares the two (Vaultwarden) gets `${MATIDENTITY_AUTHORITY%/}/` (right with either); Vaultwarden lets in only a person whose e-mail is confirmed in MatIdentity; Nextcloud's
  user_oidc and the Bitwarden web vault need https; Home Assistant (2026.8 and later) keeps its proxy settings in `.storage/http`, and undoes a change no administrator
  confirms, so its entry writes that store on the first start.

An app whose sign-in can only be set in its own administration or in a file that locks every other setting (Immich) is not connected; its note says how to do it by hand.

## Bindings and contributions (contract 2)

An entry says nothing about what the app provides, consumes or adds to the home screen: the app says it itself in its `/api` (MatDesign docs/PLATFORM.md "Capabilities",
"Bindings", "Contributions"), and the broker (`BindingBroker`) reads it after the install. For an entry with `platform: true` the broker adds, to the UI service and the `.env`:

- the variables of its bindings (`MAT_OFFICE_EDIT_URL`, `_INTERNAL_URL`, `_KEY` for an app that consumes `office.edit`, ...), passed as `NAME: ${NAME:-}`;
- `MAT_INTERNAL_URL` (where the other apps reach it on the shared network) and `MAT_PUBLIC_URL` (only when it is a real address: a route under the domain, or this host by the
  address of the settings).

Do not list these in the compose file: the entry stays the same whatever is installed beside the app. A variable the entry declares itself wins over the broker's.

## What MatOS adds

MatOS renders the entry into one compose file (`ComposeRenderer`). For every service of an app it sets:

| | |
|---|---|
| `container_name` | `matos-<app>-<service>`: a stable name on the shared network |
| labels | `matos.managed`, `matos.app`, `matos.instance`, `matos.service` |
| networks | `default` plus the shared `matos` network, for the UI service and the services listed in `shared` |
| `ports` | `<host port>:<ui.port>` for the UI service, **only while no reverse proxy routes the app**; with Matcad the app publishes no port: under a domain it is `<app>.<domain>` on 80/443, without one Matcad's route service (`routes.service`) publishes `<host port>:<host port>` for it. The host port is picked by MatOS (`ui.hostPort` first, else a free one of the pool); nobody chooses it |
| `x-matdock` | one block for the stack (name, description, category), so MatDock's launchpad shows it as an app |
