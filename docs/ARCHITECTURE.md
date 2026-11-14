# Architecture and decisions

This page records the questions that shaped Mattiverse and the answers, so they do not have to be argued again.

## What Mattiverse is: a layer on top of five engines

Mattiverse is **not** a second Docker manager with its own copy of everything. It is the thin, friendly layer on top of five programs
that each **own** one part of the configuration:

| Engine | Owns | Mattiverse adds |
|---|---|---|
| **MatDock** | stacks (compose + env), containers, backups, schedules, updates | the store, one-click installs, the launcher |
| **Matcad** | routes, certificates, DNS providers for certificates, logins | a route `app.example.com` for every app |
| **Matddns** | sources of the IP, DNS records, rules | the DNS record `app.example.com` for every app |
| **MatIdentity** | people, groups, the apps and who may open them | one sign-in for every app (a client per app) and for Mattiverse |
| **MatSMTP** | relay users, the accounts mail leaves through, rules, the queue | one relay user per app that sends mail (`mail: {}`) |

Mattiverse itself keeps what no engine knows: users, roles and sessions, shared links, the catalog, the wizard's choices, which catalog
entry an app came from, and where it sits on the home screen. Everything else lives in the engines and **is visible and editable there**: open
MatDock and the apps are stacks marked *Managed by Mattiverse*, open Matcad and they are routes, open Matddns and they are records, open MatIdentity and they are apps, open MatSMTP and they are relay users.

The five are **fixed parts** of Mattiverse (`EngineIds.All`, `core: true`; the wizard installs them in that order, MatDock first because it deploys the others; they cannot be uninstalled from Mattiverse except a failed install, to start over).
Other tools in the store (Nginx Proxy Manager, Portainer, ...) are plain apps without deep integration. The contract between Mattiverse and the
engines is [ENGINES.md](ENGINES.md).

| | Mattiverse | MatOS | MatDock |
|---|---|---|---|
| Idea | A launcher: one app at a time, like a phone | A desktop in the browser: windows, taskbar | A Docker manager for many hosts over SSH |
| Strength | Installing the Mat apps, the setup wizard, mobile | Rich desktop, file explorer, task manager | Stacks, volumes, backups, terminal, many hosts |

## How an app is installed, updated and removed

1. The page validates the form and records the app (`InstalledApp`, state *installing*); a **job** does the rest (see below).
2. `ComposeRenderer` turns the catalog entry into **one compose file**: the entry's services plus what only Mattiverse knows (container
   names, labels, the shared network, the published port of the UI) and an `x-matdock` block, so MatDock's launchpad shows the stack as an app.
   Next to it a `.env` with the answers of the form.
3. `StackEngines` picks the engine that **runs** the stack: **MatDock** once its API answers (`apiVersion >= 1`), else Mattiverse's own
   `docker compose` runner (`LocalComposeEngine`). MatDock itself is always deployed by the local runner, because nothing can deploy it before it exists.
   The app remembers its engine (`InstalledApp.Engine`).
4. **The images first** (`ImagePreflight`): Mattiverse pulls every image of the stack itself. One that cannot be pulled stops the install there, with what the
   registry said and what to do ("it is private, or it was not published yet"), before the engine has a stack of the app: a retry or a removal finds nothing half made.
   An image this machine has a copy of is used from there when the registry refuses it or does not answer (built on the host, no internet); `pull_policy: never` is
   taken from this machine only.
5. The stack is created in MatDock (`PUT /stacks/mattiverse-<app>`, `managedBy: mattiverse`) and deployed without a second pull; the output of MatDock's operation is
   forwarded to the job's log. A deploy for new variables (a binding) does not pull either: only *Update* brings newer images.
6. The provisioners and publishers then connect it: the route in Matcad, the record in Matddns.
7. The **broker** binds it (MatDesign docs/PLATFORM.md "Bindings", docs/APP-MODEL.md): see "Bindings between the apps" below.

**A port that another container holds does not fail the install** when a variable names it (MatSMTP's `SUBMISSION_PORT`): the app gets the next free port and the log says so; a port written as a number (Matcad's 80 and 443) stays, with a warning.

**A failed install can always be tried again or removed.** *Try again* runs the install once more (a port taken since is planned anew). *Uninstall* of an app
whose install failed does not depend on what is missing: an app without containers has no final backup (there is nothing to back up), a backup that fails is a
warning, a route or DNS record that cannot be removed is a warning, and when the engine cannot remove the stack but no container of the app is left, Mattiverse
removes the app all the same (and its volumes when the data is to go). A job that Mattiverse's restart interrupted leaves the app *failed*, not *installing*.

**Reading goes through MatDock too** (the owner's rule of 2026-11-14: Mattiverse asks the engine and does nothing itself; before that every read went
straight to Docker). What the pages show of the runtime comes from `RuntimeReader`: the containers and the state of the apps (`GET /stacks`, the stacks named
`<prefix>-<app>`), the log of a container (`GET /containers/{name}/logs`) and the host (`GET /environments`: the version of Docker, processor, memory, disk).
Docker is read directly (the labels `mattiverse.managed`, `mattiverse.app`, `mattiverse.instance`, `mattiverse.service`) only while there is nobody to ask:
before MatDock is installed, while it does not answer, and inside a job (the runner checks what it just deployed), so the launcher never goes blind.

**The user may change the stack in MatDock.** Mattiverse stores a semantic hash of the compose it deployed (`ComposeSha`: parsed YAML, key order
and formatting do not matter). An update compares it with the stack as MatDock has it now; if they differ the update **keeps the user's version**
(pull and re-create, no rewrite) and says so in the log. In the Files tab the app shows the stack as its engine has it.

**Without an engine API** (an older released image, or MatDock not installed yet) everything keeps working through the local runner; the
Engines tab in the settings says which engine is connected.

**After a lost database or a restore in MatDock**, `AppReconciler` takes the stacks that MatDock holds with `managedBy: mattiverse` back as apps:
the stack carries everything (catalog block, compose, variables).

## Domain and DNS

The wizard asks **once** for the domain of the apps and the account of the DNS provider (Cloudflare, Hetzner, Netcup: the providers that both Matcad and
Matddns support). The credentials are stored protected (Data Protection) and handed to both engines:

- **Matcad** gets the provider and a wildcard route `*.domain` with it: one certificate through the DNS challenge, no open port 80 needed (`ProxyPublisher`: the base
  domain, the provider, the wildcard route and Mattiverse's own route `mattiverse.domain`). Every app gets a route `app.domain` served from that certificate: a binding
  of `web.route` the broker makes for every app with a web page (Matcad is the provider; the app gets `MAT_PUBLIC_URL`, Mattiverse keeps it as the app's public address).
  An older Matcad without the bindings gets the routes the old way. Without a DNS account only the single routes exist (HTTP challenge).
- **Matddns** gets a source *public IP*, a domain group with a record for every app that has a route and a rule per record, so the record follows the home IP
  (`DnsPublisher`, after the bindings). The records are not a capability: the account, the zone and the list belong to Mattiverse's domain, and a record gives an app nothing.

**Every app is reached through Matcad** (the owner's decision, 2026-11-09), with or without a domain. Under a domain the binding of `web.route` is a host route
`app.domain`; without one it is a **port route**: Matcad's Caddy listens on the app's host port (picked by Mattiverse, `PortPlanner`; nobody chooses it) and
proxies to the app's internal address, with `publicHost` (the address of the settings) when there is one, so the app learns its address only when it is real. An app
with a route publishes no port of its own (`BindingBroker.PortsAsync`, `ComposePorts`); Matcad publishes the ports of its port routes on its route service
(`routes.service` of its catalog entry, `caddy`). A redeploy deploys the apps first and Matcad last, so an app gives up its port before Matcad publishes it, and
deploys Matcad whenever the ports it publishes are not those of its port routes (an app came, went, or moved under a domain). A route Matcad refuses (an older Matcad
without port routes, a port taken by a route made by hand) is dropped and the app publishes its own port again. Matcad's own UI keeps its own port. Installed apps open in
the window of the home screen (`js/os.js`; *Always open in a new tab* in the menu of an app), the address served by Matcad.

When the domain or the account changes (also when the domain is removed), or from the Engines tab (*Connect again*), the job `ConnectEngines` brings both in line
again and removes what Mattiverse created earlier and does not want any more (routes of an old domain; without a domain every route Mattiverse made, and the bindings
of `web.route` move from hosts to ports and back; the old cleanup leaves routes that are bindings alone).

## Where are the backups?

**In MatDock**, which owns backups and schedules: a bundle of all volumes, the compose and the `.env`, with retention. They show up in MatDock's
backup and schedule lists and are started from Mattiverse's *Backups* page (`BackupHub`, ids `matdock:<id>`).

The apps that Mattiverse runs itself (MatDock, and everything while MatDock does not answer yet) are backed up by Mattiverse's own runner
(`LocalBackupProvider`, ids `local:<id>`): each volume is copied with a throw-away `busybox` container, the manifest keeps the compose files, the
variables and the image digests, so a local backup restores into a fresh Mattiverse. The two lists are merged on one page; an *All apps* schedule runs
in MatDock, a schedule for an app runs where the app runs.

- **Consistent by default.** Containers are stopped while the volumes are copied and started again afterwards, whatever happens. A live copy is
  possible and marked as such.
- **Restore** first backs up the current state (switchable) and re-creates the app even when it was uninstalled.
- **Download** as a `.tar` to copy a backup off the host. Remote targets (SMB, NAS) are MatDock's backup targets.
- Limits: backups contain secrets (the `.env`), so protect the location like the volumes; no encryption.

## Where are the compose files?

Of the stacks that MatDock runs: **in MatDock** (below its data volume), one text per stack, which is what the user sees and edits there.
Of the stacks that Mattiverse runs itself: on the Mattiverse data volume, `/data/apps/<app>/` with `docker-compose.yml` (the rendered file) and
`.env`, so `docker compose -p mattiverse-<app> -f ... up -d` works by hand.

**Rule for catalog entries:** use named volumes, not relative bind mounts. `docker compose` runs inside a container (Mattiverse's or MatDock's), so a relative
path would resolve inside that container and not on the host.

## Where does the catalog live?

**In Mattiverse** (`src/Mattiverse.Core/catalog/apps/*.yml`, embedded, plus `/data/catalog/*.yml`). A catalog entry is a compose file with an
`x-mattiverse` block ([CATALOG.md](CATALOG.md)): data, not code. MatOS keeps its catalog in C# code, which makes every new app a release; here it is a file.
A user file with the same `id` replaces the built-in entry. The three engines carry `core: true` and a `provision` key that names what connects them.

All apps and engines share the Docker network `mattiverse` with Mattiverse itself (it attaches itself on start), so they reach each other by container name
(`mattiverse-<app>-<service>`). If Docker's address pools are used up, `Mattiverse__NetworkSubnet` gives the network an explicit subnet.

## How a long task runs

Nothing slow runs inside a web request. The page validates and records, then queues a **job** (`Job` table): a single
background worker runs jobs one after the other (Docker operations on one host must not overlap), keeps the output in
memory while it runs (the page polls `/api/jobs/<id>/log`) and writes it to the database every few seconds and at the end.
A job that was running when the container stopped is marked failed on the next start, and so are the apps that were
being installed.

## Authentication and roles

- Local accounts, passwords with PBKDF2 (210,000 iterations) and a constant-time comparison; the sign-in does not reveal
  whether a user exists.
- A **session** is a row in `UserSession` (a `Token` UUID in the cookie). Sessions live in SQLite on the volume and the
  Data Protection keys are persisted next to it, so **sessions survive a container restart**.
- Roles: `Admin`, `User`, `Anonymous`. Anonymous is not an account: it is the visitor of a **shared link**
  (`/s/<token>`), which shows one app. Pages are secure by default (`FallbackPolicy` requires a user); the
  admin pages are listed in `Program.cs`.
- Microsoft Entra ID: the sign-in is isolated in `AuthService` and `SessionService`; adding an OpenID Connect scheme
  next to the cookie and mapping the claims to `User` is the planned step.
- The engines' API keys are generated by Mattiverse when it installs them, stored with the app's variables (protected) and sent as `X-Api-Key`.
  An engine without a key answers `503`, so its API is never open.

## Database conventions

SQLite (`mattiverse.db`) holds the logic, JSON (`config/system.json`) the configuration.

- Tables are PascalCase and singular (`User`, `UserSession`, `InstalledApp`, `Job`, `Backup`, `BackupSchedule`, `ShareLink`).
- The primary key is always `Id` (64 bit). A table with a security key has a `Token` (UUID): `UserSession`, `ShareLink`.
- Every row has `CreateDate`, `CreateUserId`, `UpdateDate`, `UpdateUserId` and `UpdateState` (0 deleted, 1 created, 2 updated).
  `Remove` becomes a soft delete and a global query filter hides deleted rows; unique indexes ignore deleted rows.
- Schema changes are EF Core migrations (`src/Mattiverse.Core/Data/Migrations`), applied at start.
- `Backup` and `BackupSchedule` only hold the backups and schedules of Mattiverse's own runner; those of MatDock live in MatDock.

## UI conventions

Mattiverse uses **MatUi**, the UI toolkit of [MatDesign](https://github.com/Real-TTX/MatDesign), so that it looks and behaves like the other Mat apps; the rules are MatDesign's rulebook.

- **The toolkit is a copy** in `src/Mattiverse.Web/wwwroot/matui` (CSS, one script, the icon sprite): the app works offline and as a PWA, and nothing is loaded from another site.
  `scripts/update-matui.ps1` brings a newer release from MatDesign. The app adds only what is its own: `mv-*` classes (app icons, the launcher of the home screen, the app frame, the wizard) in `mattiverse.css`
  and a small script (clock, the search of the home screen, job log, wizard).
- **Shell:** the layout draws the menu (one menu for a user; Administration is a second menu behind the switcher for an administrator; Settings is a submenu), the user menu, the 3-state theme button,
  the version and the breadcrumb. Sign-in, setup, shared links and errors have no menu (`_Bare`). The theme is kept on the account (`/api/theme`) and in the browser.
- **Controls:** the tag helpers `<icon>` (a use of the sprite), `<app-icon>`, `<mat-field>` and `<tab-bar>` and the partials `_Toolbar`, `_Pager`, `_ValidationSummary` and `_StatusPill` are the one code base for them.
- **Lists:** the page model builds a `ListView` (the rows of the page, a `ToolbarModel`, a `PagerModel`) from the query string. The toolbar has a search field, Filter and Sort open dialogs. Every list has its toolbar
  on top, the pager directly under the table and its actions below, left-aligned.
- **Forms:** CRUD pages are a list and a separate page for create/edit. Buttons stand in one line from positive to negative: *Save, Back, (space) Delete*. The page validates (`novalidate`: no bubbles of the browser);
  a summary at the top links to the fields. After a save the page redirects and shows a toast. Dependent fields are shown only when needed (`data-show-if`).
- **The logo** stays the planet with its orbit, not the flat glyph tile that MatDesign's rule IDENT-03 describes: the owner prefers it.
- Mobile first where it counts: a drawer for the menu, tables become cards, dialogs are full screen, safe-area insets for notches.

### The home screen: Mattiverse OS

The home screen is a **small operating system** (the owner's choice of 2026-11-08/09, modelled on iOS, umbrelOS and MatOS), not a page of the toolkit:

- **Its own look.** `Pages/Shared/_Launcher.cshtml` with `css/os.css` and `js/os.js`: a status bar (logo, running tasks, theme, the person, date and time), the wallpaper over
  the whole screen, widgets of glass, app icons, a search and a dock of glass. Glass, colour gradients on the icons of the system and the system font are allowed there on
  purpose; the colours are the `--os-*` properties of `os.css`. This is a deliberate exception to MatDesign (SHELL-01, SHELL-03, COLOR-06) for the home screen,
  approved by the owner like the logo: do not bring the menu of the toolkit back to it.
- **Windows, like a desktop** (the owner's choice of 2026-11-11; before that there was one window at a time). Anything with `data-window` opens that page in a window
  of its own over the home screen: an iframe (`js/os.js` makes it from the template in `_Launcher`). In there a page of Mattiverse keeps MatUi for its content and drops
  the menu, the header and the breadcrumb (`js/window-mode.js` sets `mv-in-window` on `<html>` before the page is drawn; `mv-window-only` shows what only makes sense
  there, such as the tabs of the settings). A window has a title (the page's), Back (inside the window), Reload, Open in a new tab, and three lights: red closes it,
  yellow puts it away and keeps the app as it is, green fills the screen.
  - **A page looks like a part of its window** (`css/mattiverse.css`, under `.mv-in-window`): rounder and tighter through the tokens of the toolkit, the title of the layout is
    left to the bar of the window (it stays for a screen reader), tabs are a segmented control, and the tabs of the settings and of the account (`mv-winnav`) are the
    sidebar of a window at least 720 px wide, like the settings of a desktop.
  - **Several at once.** The bar moves a window, its edges and corners size it, a click anywhere in it (also inside its page) brings it to the front; a double click on
    the bar fills the screen. Let go at the top of the screen it fills it, at the left or right edge it takes that half (the place is shown while dragging); dragged
    away it has its own size again. A new window opens a step beside the last one. A window that fills the screen ends above the dock.
  - **The dock is the taskbar** and stays above the windows: a dot under an app that has a window (a longer one for the window in front), and an icon for every
    window whose app is not in the dock, for as long as it is open. A click brings the window to the front; a click on the one in front puts it away. The logo in the
    status bar shows the desktop (every window is put away), and the status bar names the window in front.
  - **What is kept.** Every app remembers the place and size of its window in this browser (`localStorage`), and the windows of a tab come back after a reload
    (`sessionStorage`: page, place, order, put away or not). The address keeps the page of the window in front (`/#/Store`), so a link with it opens that window; the
    browser's Back shows the desktop. An address in the hash is a path of this Mattiverse only.
  - **The home screen refreshes itself in place** (`refresh()` in `js/os.js`: the things are fetched and exchanged; the rate is `data-autorefresh` while something
    runs, and a page that loads in a window asks for it once): a reload would take the windows with it.
  - **On a phone** a window fills the screen, with a bar at the bottom to go home; the windows are switched with the dock.
- **Pages that belong to the whole tab** (sign-in, a shared link, an error: `_Bare`, `<html data-top-level>`) leave the window; a link to `/` inside it puts the window away, and a page that
  lands on the home screen closes it. Opened directly (a new tab), every page still has the shell of the toolkit with its menu.
- **Everything is an app.** The parts of Mattiverse (`Support/Launcher.cs`): Activity and Users on the screen, Store, Apps, Backups and Settings in the dock (a user gets
  Apps and the account). An installed app opens in a new tab by default (its own address); its menu (a right click, a long press) opens it in the window instead
  (`/Apps/Open`, which falls back to a button when the app forbids a frame) and can make that its default in this browser (localStorage).
- **The engines are part of the system**, not apps between the others: a widget with the five and their state (from Docker), and the folder *System* with them as apps.
  An engine opens its own window in Mattiverse (`/Engines?id=`, `Support/EngineOverview.cs`): its state and version (its API), what it keeps for Mattiverse and which of
  your apps it serves (a stack, a route, a DNS record, the sign-in, a relay user), and its own pages: **here in the window** when the engine lets Mattiverse frame it,
  else in a new tab. An engine allows the addresses in `MAT_FRAME_ANCESTORS` (MatDesign docs/PLATFORM.md, "Shown inside another app"), which Mattiverse fills before
  it deploys an app whose compose file names it (`Core/Integrations/FrameLink.cs`: the route behind the proxy, `localhost` and the configured address with the port
  Mattiverse is published on, the addresses people signed in from). `Support/FrameProbe.cs` asks the app first (`FramePolicy`: `frame-ancestors` wins over
  `X-Frame-Options`, judged for the address of the page) and offers a new tab otherwise. Older engine images refuse frames: they show it after an update.
- **Widgets** are cards in the grid of the apps (two cells, or two by two): today (the time, the greeting: the page's h1), the engines, apps running, the last backup
  (amber after seven days or with none), the activity, Docker. The search above the dock narrows the apps (key `/`, Enter opens the first); the widgets step aside.
- **The wallpaper** is a picture of the account (`User.Wallpaper`, the claim `mattiverse:wallpaper`, `Core/Users/Wallpapers.cs`, the tab *Home screen* of the account).
  The default is *Orbit*, the planet of the logo, light or dark with the theme; Earth, Jungle, Lagoon and Mountains are views from above; *None* is the plain background.
  The pictures are drawn, not downloaded: `scripts/wallpapers` (SVG with noise filters, rendered to JPEG with Edge; `node scripts/wallpapers/render.js`).
- **What the apps add** (contract 2, `contributes` of their `/api`; `Support/HomeContributions.cs`, `Core/Integrations/ShellRules.cs`): an app with launchers gets
  one icon per launcher instead of its own (its picture at the app, `/Apps/Open?id=&path=` in the window, its address in a tab; a badge from a key of its state), its quick
  actions are entries of the menu of the icon (`data-quick`), and its widgets stand after the ones of the system (`stat`, `meter`, `tiles` from `/api/state`, `list` from
  `/api/widgets/{id}`; `_AppWidgetBody`). Who sees what follows `audience` (`admin` for administrators); widgets with the values of the `person` who is looking are left out
  until Mattiverse can ask an app on a person's behalf. The definitions and values are asked with a short timeout and kept a little while (`AppManifests`, `ManifestCache`),
  so a slow or stopped app does not hold the home screen up; a widget with `refresh` makes the home screen reload itself that often (at least every 30 s).
- **The person arranges it, the account keeps it** (`js/os.js`, `Core/Users/HomeLayout.cs`, `User.HomeLayout` as JSON, `PUT` / `DELETE /api/home` with the anti-forgery token
  in the header `RequestVerificationToken`). Every thing of the home screen has a key (`w:today`, `w:<slug>/<widget>`, `a:<slug>`, `a:<slug>/<launcher>`, `s:jobs`, `f:system`;
  `IndexModel.ArrangeAsync`); the layout is the pages with their keys in order, the keys taken off the screen, and the size chosen for a widget. The page draws it as the
  person left it: what is new (an app installed since) joins the end of the first page, what is gone drops out, an empty page goes, and a layout that cannot be read is the
  home screen as it comes. What is off the screen is drawn out of sight (`.os-hidden`), so that it comes back without a reload.
  - **A right click everywhere** (a long press on a touch screen; Shift with the right click is the browser's own menu, a field keeps the browser's menu): an app (open, its quick
    actions, details), a widget (open it or the row under the pointer, its size: small, medium, wide as far as it offers them), every thing of a page (one place back or on,
    to another page or a new one, off the screen, edit the home screen), the dock, the bar of a window and its icon in the dock (back, reload, a tab, fill the screen, a half, put away, close, close all) and the empty screen
    (edit, add a page, show again what is off it, the wallpaper, the store and the settings for an administrator, reload, reset).
  - **Dragging**: the mouse drags a thing at once (a copy follows the pointer, the others glide out of the way); a finger drags while editing, else it swipes the pages.
    Waiting at the edge of the screen or over a dot turns the page and takes the thing along; past the last page a new one begins.
  - **The home screen never scrolls**: it is exactly the window (status bar, pages, dock). A page shows what fits on the screen; the first thing that reaches below it and all
    after it continue on a page of their own right after it (`data-continues`, `flow()` in `js/os.js`; the grid places things in order, so an earlier thing never moves for a
    later one). The account keeps the person's pages, not the screens, so the same arrangement fits a phone and a large screen; a new size of the window lets them flow again.
  - **Pages** side by side, a sideways strip that snaps (`.os-pages`, `.os-page`): a swipe, the dots, the arrow keys. A reload (the home screen refreshes itself) comes back to
    the same page; it waits while the person edits or drags (`os-hold`). The search shows the apps of all pages in one grid.
  - **Editing** ("Edit the home screen"): the things wiggle, each has a minus that takes it off the screen (Delete does it by keyboard), the bar instead of the search has
    *Add a page*, *Show again* and *Done*; a tap beside the things ends it.
  - The dock and the folder *System* stay as they are (the dock is the parts of Mattiverse, the folder the engines).

## Bindings between the apps

The apps say what they **provide** and **consume** (capabilities, MatDesign docs/PLATFORM.md "Capabilities"); Mattiverse is the **broker** that connects them, with one
class for every capability and no code that knows an app (`Core/Integrations/BindingBroker.cs`):

- **After an install or an update** (`AppService.ConnectBindingsAsync`): the broker reads the app's `/api` (it waits up to a minute for a fresh app), binds what it consumes
  to an installed provider (`PUT /api/bindings/{capability}/{slug}` at the provider, `managedBy: mattiverse`; a consumer keeps the provider it has), and binds the installed
  consumers of what it provides. Every app whose variables changed is deployed once more.
- **The variables** of a binding are kept in `AppBinding` (protected as a whole: they hold a key) and go, with `MAT_INTERNAL_URL` and `MAT_PUBLIC_URL` (only a real address),
  into the `.env` and the web service of the consumer (`ComposeFiles.Build(..., injected)`, `ComposeRenderer` adds `NAME: ${NAME:-}`); a variable of the catalog entry wins.
  A secret the provider does not hand out again (`{"isSet": true}`) is kept; one Mattiverse lost is renewed.
- **Uninstall**: the app's bindings are removed at their providers; the consumers of what it provided get another provider or lose the variables, and are deployed again.
- The job that connects the engines also binds every app again (`ConnectAllBindingsAsync`). A provider that is deployed again in that pass may answer other values
  afterwards (MatIdentity's issuer follows its new public address when a domain comes or goes): the broker waits until it runs and binds its consumers once more
  (at most two rounds); an install or update does the same. The tab *Connections* of an app's Manage page shows `GET /api/connections` of the
  app with what Mattiverse bound.
- **The engines are providers like any app** (docs/APP-MODEL.md step 5): MatIdentity (`identity.oidc`), MatSMTP (`mail.smtp`) and Matcad (`web.route`) answer
  `/api/bindings`; Mattiverse provides `shell.embed` itself (`MAT_FRAME_ANCESTORS` for every app of the contract, `FrameLink`). What an app consumes comes from its catalog entry
  (`consumes`, for images that do not say it; the old keys `identity: {}` and `mail: {}` are read into it) and its `/api`, plus the implicit `web.route`.
- **Engines older than the bindings** keep working: the variables an earlier Mattiverse filled (`MATIDENTITY_*`, `MATSMTP_*`) are taken over as bindings at the app's next deploy
  (`BindingBroker.AdoptAsync`), and a binding whose provider is still installed but does not answer `/api/bindings` is kept. A new app gets such a binding only from an engine
  image with the bindings.
- What stays engine code in Mattiverse is the **setup of the engines themselves**: MatDock as the runtime, Matcad's domain, DNS account and wildcard route, Matddns' records,
  MatSMTP's first-run setup and sender domain (`MailLink.ConfigureRelayAsync`), Mattiverse's own sign-in and the issuer of MatIdentity (`IdentityLink`). MatAi
  (`ai.openai`) follows when its branch is merged.

## What is deliberately not here

- **No `mssql-server` / `pgadmin` service.** The project template names them; with SQLite neither is needed. The release
  stack is the single service `mattiverse`; the dev stack adds a SQLite web viewer.
- No multi-host management (that is MatDock), no file explorer or terminal (MatOS / MatDock).
- No deep integration of alternatives (Nginx Proxy Manager, Portainer): they are installed as plain apps.

## MatIdentity, the single sign-on

MatIdentity is an engine (the fourth card of the wizard, `core: true`), and **Mattiverse manages all of it**. People, groups and who may open which app live in
MatIdentity; Mattiverse administers them in its area **People** (below) and MatIdentity's own pages do the same.

- **Its address.** Mattiverse gives MatIdentity its public address as the fixed issuer (`MATIDENTITY_ISSUER`: the route of the proxy, else this host and
  its port). Inside Docker the apps reach it as `http://<project>-matidentity-matidentity:2300` (`MATIDENTITY_INTERNAL_URL`); MatIdentity names the public
  address in its discovery document whichever way it is read, so the browser always goes to the public one.
- **The apps.** An app that consumes `identity.oidc` (its catalog entry, or its `/api`) is bound by the broker: MatIdentity makes the client (id = the slug,
  `managedBy: mattiverse`, redirect addresses for every public address of the app, the back-channel logout at its internal address) and answers the `MATIDENTITY_*`
  variables (MatDesign docs/PLATFORM.md "`identity.oidc`"); the binding is removed with the app. When MatIdentity is installed (or removed) the apps that are there are
  bound (or lose it) and redeployed; *Connect again* on the Engines page does it for all.
- **Back-channel logout.** Signing out at MatIdentity (or an administrator ending a person's sessions there) ends the person's sessions in every app: MatIdentity
  posts a signed logout token (OpenID Connect Back-Channel Logout 1.0) to the client's `backchannelLogoutUri`. MatIdentity registers it for every bound app as the
  app's **internal** address on the shared network, `<consumer.internalUrl><backchannel>` (the param, default `/signout-backchannel`), since it calls it server to
  server; an app without a web service or port gets none. The
  apps on MatPlatform 1.4.0 answer it (MatDesign `src/MatPlatform`, `IMatIdentitySessions`); an older app answers 404 and keeps its session until it expires.
  Mattiverse registers its own as `http://<its container>:<its port>/signout-backchannel` (`SelfBackchannelLogoutUriOf`; none when it runs outside Docker).
- **Mattiverse itself** is the client `mattiverse` (it may call every app). The login page goes straight to MatIdentity when it is connected (`?local=1`
  keeps the local form); `/Account/External` turns the answer into a session: the person is found by `sub` (`User.ExternalSubject`), else by the user
  name, else created, and the role follows the claim `role`. Signing out ends the session at MatIdentity too. The OpenID Connect options are read from the
  settings when they are built, so nothing needs a restart (`MatIdentitySignIn`). **The sign-in follows MatIdentity's address:** MatIdentity's issuer is its public
  address (the address of this machine with its port, or its name under the domain), so an install or an update of MatIdentity registers Mattiverse again
  (`IdentityLink.ConnectSelfAsync`). The login page first checks that the discovery document and the keys can be read (`MatIdentitySignIn.ReadyAsync`); when not,
  and MatIdentity answers, Mattiverse registers itself again and the page comes once more (`?reconnected=1`; the sign-in of a request is set up when it begins);
  a MatIdentity that does not answer leaves the settings alone and the page offers the local sign-in with a message instead of an error. The session keeps the claim `sid` of the ID token (`UserSession.IdentitySessionId`);
  `POST /signout-backchannel` (`Infrastructure/BackchannelLogout.cs`, anonymous, no anti-forgery) checks the logout token (`Core/Auth/LogoutToken.cs`, the same checks
  as MatPlatform's: signature with the keys of the handler's discovery document, `typ` `logout+jwt`, `iss`, `aud` = `mattiverse`, `iat` at most five minutes old,
  `exp`, the back-channel event, `sub`/`sid`, no `nonce`, a `jti` replay cache in memory) and ends the sessions (`SessionService.EndIdentitySessionsAsync`): with `sid`
  the sessions that began with it, with `sub` alone every session of the linked user, local ones too. Sessions live in SQLite and the cookie is checked against
  its session on every request, so the next request is signed out. `200`; `400` for an invalid token; `404` while Mattiverse is not connected; `no-store`.
- **People** (MatDesign docs/ACCESS.md, PLATFORM.md "People, permissions and quotas"). `/People` (administrators) is a front end of MatIdentity, Mattiverse keeps no people
  of its own there: `Core/Integrations/MatIdentityPeople.cs` speaks SCIM 2.0 with MatIdentity (`/scim/v2`: users, groups, the extension `urn:mat:scim:schemas:extension:access:1.0`
  with the grants and what a person ends up with per app) and `/api/v1` (the apps with their roles, permissions and quotas; the actions on a person), with MatIdentity's key,
  naming the person who acts (`X-Mat-Actor`). The pages: People (list, a person: details, groups, the section Apps, the actions), Groups, and Access (what a person or group
  is given in one app: whether at all, the role, the permissions, the quotas, the usage the app reports at `/api/quotas/{subject}`, `PlatformClient.GetQuotasAsync`). Every
  form is drawn from what MatIdentity keeps of the app, which it read from the app's `/api`: **no page knows an app**. Mattiverse's own accounts (the local administrator,
  the way in when MatIdentity is down) are the tab *Mattiverse accounts* (`/Users`). Without MatIdentity the area says what is missing.
- **What each person sees.** `PersonAccess` asks MatIdentity (SCIM) which apps a person may use and their role in each, and keeps it a minute (the People pages forget
  it after a change). The home screen and the list of apps show a person only those apps (an app that does not sign in through MatIdentity stays, it has users of its own),
  `/Apps/Open` refuses the others, and the launchers and widgets follow the person's role in the app: `roles` of a contribution decide when the role is known, `admin` is
  the app's first role (`ShellRules.Sees`). An administrator of Mattiverse sees every app; a local account and a person MatIdentity says nothing about see what
  they always saw (the rules of `audience`).
- **Manage.** An app with `platform: true` gets the tab *Manage*: `PlatformClient` reads its MatApiDefinition (`GET /api`) and its values with `MAT_API_KEY`,
  and the page draws its pages (tiles, settings form, buttons) with MatUi without knowing the app.

## MatSMTP, the mail relay

MatSMTP is the fifth engine (the last card of the wizard, `core: true`): every app that sends mail hands it to MatSMTP, and MatSMTP delivers it through the account set
in its own UI. **Mattiverse manages the connection**; the account, the rules and the queue stay in MatSMTP's pages (MatDesign docs/PLATFORM.md "Mail through MatSMTP", the
API in [ENGINES.md](ENGINES.md)).

- **Its setup.** The catalog entry generates `MATSMTP_API_KEY` and the administrator's password (`MATSMTP_ADMIN_PASSWORD`); after the install `MatSmtpProvisioner` sets
  `setupCompleted` and the domain of the senders (the domain of the wizard). Only the submission port is published, for devices in the network; the SMTP port of the apps is
  reached on the shared network only, so MatSMTP is never an open relay.
- **The apps.** An app that consumes `mail.smtp` (its catalog entry, or its `/api`) is bound by the broker: MatSMTP makes a relay user (id = the slug, `managedBy: mattiverse`,
  the sender `<sender>@<domain>` as the only allowed one) and answers the `MATSMTP_*` variables (its container name, port and TLS mode, the user, the password once, the
  sender; MatDesign docs/PLATFORM.md "`mail.smtp`"). Mattiverse keeps the password and asks for a new one only when it has none. Without MatSMTP the variables are emptied and
  the app sends its mail itself; the relay user is removed with the app. When MatSMTP is installed (or removed) the apps are bound (or lose it) and redeployed.
- **The apps' side.** With the variables set an app sends all its mail through MatSMTP and shows its own SMTP settings as fixed (in its pages and with `fixed: true` in its
  `/api/configure`, which the Manage tab draws as disabled fields and never sends back). MatPlatform 1.3.0 (`MatSmtpSettings`) does this for the .NET apps.
- **Mattiverse itself** sends no mail today, so it has nothing to connect.
