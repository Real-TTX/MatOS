# The app catalog

An app in the store is a normal **`docker compose` file** with one extra top-level block, `x-mattiverse`. Compose ignores
`x-*` keys, so the same file also runs with plain `docker compose up`.

Built-in entries live in [`src/Mattiverse.Core/Catalog/apps`](../src/Mattiverse.Core/Catalog/apps) and are embedded in the
image. Your own entries go to **`/data/catalog/*.yml`** (the data volume); use **Store → Reload catalog** to pick them up.
A file with the same `id` as a built-in entry **replaces** it, and a file that cannot be parsed is skipped and logged.

## Example

```yaml
x-mattiverse:
  id: wiki                    # lowercase letters, digits, dashes; the folder and project name derive from it
  name: Wiki
  tagline: Notes for the family
  description: A longer text for the install page.
  category: Productivity      # groups the store
  role: app                   # app | proxy | ddns | docker | identity | mail  (the role of an engine: Matcad, Matddns, MatDock, MatIdentity, MatSMTP)
  icon: wiki.svg              # a file below wwwroot/img/apps/, an https URL, or leave out for a generated tile
  homepage: https://example.com/wiki
  recommended: false          # offered first in the store (infrastructure roles only)
  core: false                 # true for the five engines: installed by the wizard, cannot be uninstalled
  ui:
    service: wiki             # the compose service with the web UI
    port: 3000                # its port inside the container
    hostPort: 3000            # tried first on the host; a free port from the pool is used when taken
    path: /login              # optional: appended when the app is opened
    signIn: /Account/Login    # optional: where its sign-in starts; with MatIdentity the app is opened here, so the person lands in it without a click
  shared: [proxy]             # more services that must be reachable by the reverse proxy (the UI service always is)
  routes:                     # only for the reverse proxy (a provider of web.route): the service that listens for the routes;
    service: caddy            # without a domain Mattiverse publishes the port of every app here (the apps publish none)
  provision: matcad           # optional: a built-in provisioner to run after the install (matdock | matcad | matddns | matidentity | matsmtp)
  platform: true              # optional: the app speaks the platform contract (MatDesign docs/PLATFORM.md): Mattiverse gives it MAT_API_KEY and a Manage page
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

volumes:
  wiki-data:
```

## Rules

- **Do not publish the UI port in the compose file.** Mattiverse publishes it itself (`ui.port` on a free host port) in the
  rendered compose file. Ports that are fixed on purpose (80/443 of a proxy, the mail ports of MatMail) are written in the
  compose file; Mattiverse checks them before the install and warns when another container holds them.
- **Named volumes only.** A relative bind mount would resolve inside the Mattiverse container.
- **Answers reach compose as `${KEY}`** from the app's `.env`. Besides your variables you can use `${MV_SLUG}`,
  `${MV_PROJECT}`, `${MV_PUBLIC_HOST}` and `${TZ}`. Values must not contain a single quote or a line break.
- Every `${KEY}` in the compose file needs a variable or a default (`${KEY:-default}`); the unit tests check this for the
  built-in entries.
- A catalog entry with `provision: matcad` (or `matddns`, `matdock`, `matidentity`, `matsmtp`) connects the engine after the install; other provisioners are C# classes
  implementing `IAppProvisioner`. An engine also needs its API key as a generated, hidden variable (`MATCAD_API_KEY`, `MATDDNS_API_KEY`,
  `MATDOCK_API_KEY`, `MATSMTP_API_KEY`; MatIdentity uses `MAT_API_KEY`) that the compose file passes to the container the way the engine expects it (see [ENGINES.md](ENGINES.md)).
  The five engines are `core: true` (the wizard installs them, the app list cannot remove them); `EngineIds.All` names them in the order they are installed.

## The platform contract and what an app uses

- `platform: true` adds the hidden, generated variable `MAT_API_KEY`; the compose file passes it as `MAT_API_KEY: ${MAT_API_KEY}`. The app gets a tab **Manage**, drawn from its `/api`.
- `consumes` lists the capabilities the app uses (`capability`, `version` (default 1), `params`): `identity.oidc` for the sign-in through MatIdentity, `mail.smtp` for its mail
  through MatSMTP, or any other (MatDesign docs/PLATFORM.md "Capabilities"). The broker binds each to an installed provider and passes the binding's variables
  (`MATIDENTITY_*`, `MATSMTP_*`, ...) to the app's web service and its `.env`; without a provider they are empty and the app does it itself. An app whose image says what it
  consumes in its own `/api` needs no entry (what the `/api` says is added; it wins for the same capability). `identity.oidc` needs a `ui`.
- The old keys `identity: {}` (with `callback`, `signedOut`, `backchannel`) and `mail: {}` (with `sender`) are read as `identity.oidc` and `mail.smtp` with those parameters, so a
  catalog copy of an earlier Mattiverse keeps working. The variables an earlier Mattiverse filled for them are taken over as the app's bindings at its next deploy.
- A service other than the web service that needs the variables (the one that sends the mail) names them in the compose file (`MATSMTP_HOST: ${MATSMTP_HOST:-}`): the values are in the
  `.env`. The web service gets them by itself.

## Bindings and contributions (contract 2)

An entry says nothing about what the app provides, consumes or adds to the home screen: the app says it itself in its `/api` (MatDesign docs/PLATFORM.md "Capabilities",
"Bindings", "Contributions"), and the broker (`BindingBroker`) reads it after the install. For an entry with `platform: true` the broker adds, to the UI service and the `.env`:

- the variables of its bindings (`MAT_OFFICE_EDIT_URL`, `_INTERNAL_URL`, `_KEY` for an app that consumes `office.edit`, ...), passed as `NAME: ${NAME:-}`;
- `MAT_INTERNAL_URL` (where the other apps reach it on the shared network) and `MAT_PUBLIC_URL` (only when it is a real address: a route under the domain, or this host by the
  address of the settings).

Do not list these in the compose file: the entry stays the same whatever is installed beside the app. A variable the entry declares itself wins over the broker's.

## What Mattiverse adds

Mattiverse renders the entry into one compose file (`ComposeRenderer`). For every service of an app it sets:

| | |
|---|---|
| `container_name` | `mattiverse-<app>-<service>`: a stable name on the shared network |
| labels | `mattiverse.managed`, `mattiverse.app`, `mattiverse.instance`, `mattiverse.service` |
| networks | `default` plus the shared `mattiverse` network, for the UI service and the services listed in `shared` |
| `ports` | `<host port>:<ui.port>` for the UI service, **only while no reverse proxy routes the app**; with Matcad the app publishes no port: under a domain it is `<app>.<domain>` on 80/443, without one Matcad's route service (`routes.service`) publishes `<host port>:<host port>` for it. The host port is picked by Mattiverse (`ui.hostPort` first, else a free one of the pool); nobody chooses it |
| `x-matdock` | one block for the stack (name, description, category), so MatDock's launchpad shows it as an app |
