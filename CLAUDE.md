# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, persistence,
interfaces or major data flow change.

## What MatOS is

A launcher (iOS/Android look, windows like a desktop) that **installs, updates and backs up Docker apps**. It is a thin layer on top of
five **engines** that own the configuration and show it in their own UI: **MatDock** (stacks, backups, schedules), **Matcad** (routes,
certificates, DNS providers), **Matddns** (IP sources, DNS records, rules), **MatIdentity** (the single sign-on: a client per app, docs/ARCHITECTURE.md "MatIdentity")
and **MatSMTP** (the mail relay: a relay user per app that consumes `mail.smtp`, docs/ARCHITECTURE.md "MatSMTP"). `EngineIds.All` names them in the order they are installed
(MatDock first, MatSMTP last); all are `core: true`. MatOS calls their REST APIs (`docs/ENGINES.md`); the wizard
installs them and asks once for the domain and the DNS account. They are fixed parts, not options. Every app is a compose project.

Stack: ASP.NET Core on **.NET 10**, C#, **Razor Pages** + TagHelper controls, EF Core + **SQLite**, the `docker` CLI against the mounted
socket, plain JavaScript (no build step), Docker-first. Web port **8030**.

The look and the controls are **MatUi**, the toolkit of [MatDesign](https://github.com/Real-TTX/MatDesign) (the design and identity of all Mat apps, port 8060): the pages are built from its classes and follow its rulebook. Look there (the Components page, the Shell page, the rules) before building any UI.

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before changing the design: it records the decisions (the engines own the
configuration, reading and changes go through the engines, catalog as data, ...). [docs/ENGINES.md](docs/ENGINES.md) is the API contract
with MatDock, Matcad, Matddns, MatIdentity and MatSMTP, [docs/CATALOG.md](docs/CATALOG.md) the format of a catalog entry (`platform`, `consumes`),
[docs/INTEGRATION.md](docs/INTEGRATION.md) the state of the sibling repositories (the `mattiverse-integration` branches).
The apps connect to each other through **bindings** (MatDesign docs/PLATFORM.md contract 2, docs/APP-MODEL.md): MatOS is the broker (`BindingBroker`) and the shell (the
apps' launchers, quick actions and widgets on the home screen). **No code that knows a particular app**: a new pair of apps needs no change here.

## Build / run / test

- **Build:** `dotnet build MatOS.slnx` (note: `.slnx`). SDK 10 (see `global.json`). Expected: 0 warnings.
- **Unit tests:** `dotnet test` (xunit; no Docker needed: catalog parsing, generated compose files, variables, ports, schedules,
  `docker ps` parsing, a stale EF migration lock (`MigrationLockTests`), the rules of the first start (the phases, the needs of a line, its dependents for Try again, the records to create, the list Getting started), and `WebUiTests`: the pages name only icons of the toolkit sprite, have no inline style or script, and the stylesheet has tokens only).
- **Browser checks:** `cd tests/e2e && npm install && node run.js [http://localhost:8030]` (Playwright, installed Edge) against the running dev stack: every page light, dark and on a phone,
  the theme button, the list tools, validation, the flash message, the wizard, the drawer. It removes what it creates.
- **Run / test the app: always rebuild the container and redeploy the stack** (project rule): `./scripts/redeploy.ps1`
  (`-Fresh` wipes the volume, `-Port` changes the host port). Dev stack: `docker-compose.dev.yml` (app + `sqlite-web` on 8021).
  In `Development` the account `admin@example.com` / `Start12345` exists (the test account of every Mat app).
- **Release stack:** `docker-compose.yml`, image `ghcr.io/real-ttx/matos`, one service and one volume: keep it that simple.
- **MatOS Services** (`services.mattiverse.de`, the free address `<name>.my.mattiverse.de`; contract docs/SERVICES.md): its own project and image `ghcr.io/real-ttx/matos-services`
  (`Dockerfile.services`, `deploy/services/` with the setup on the owner's server1). It runs on the internet: deploy it only when the owner says so.
  A free address stays in the tunnel until `MatOS__ServicesDirectAvailable` is true (the service offers the direct way; `HostedAddressService.DirectAvailable`).
- **Real tests are end-to-end against Docker.** The pattern that worked: install apps through the UI with Playwright
  (installed Edge, see the `screenshots` skill), then check with `docker ps` / `docker exec` / `curl`. Remember
  `MSYS_NO_PATHCONV=1` for Git Bash arguments that start with `/`.
- **The development PC has all Docker address pools used up** (`all predefined address pools have been fully subnetted`).
  To test installs there, put an explicit `networks: default: ipam: config: - subnet: 10.253.x.0/24` into the catalog copy
  under `/data/catalog` and give each app its own subnet, and set `MatOS__NetworkSubnet` for the shared network. The web port is 8030
  (8020 is Schulio's); `MATOS_PORT` changes the host port.
- **Testing the engines:** run a second MatOS (`docker run`, own volume, port 8130) with catalog copies in `/data/catalog` that use the locally built
  images (`matdock:mattiverse`, `matcad:mattiverse`, `matddns:mattiverse`, built from the `mattiverse-integration` branches), `pull_policy: never`
  (compose cannot pull a local image), other host ports (the PC has a real MatDock on 4455, Matcad on 4433) and own subnets. Do not touch the
  other containers on the PC.
- **Updating the toolkit:** `./scripts/update-matui.ps1` copies `matui.css`, `matui.js`, `matui.boot.js` and `matui-icons.svg` from a running MatDesign (default http://localhost:8060) into
  `wwwroot/matui`. The files are a copy (rule CODE-03): never edit them here, a change belongs into MatDesign. The version is the first line of `matui.css`.
- **Changing engine behaviour:** the engines' own repositories are where it happens (branch `mattiverse-integration`); keep `docs/ENGINES.md` and the
  clients (`MatDockClient`, `MatcadClient`, `MatddnsClient`, `MatIdentityClient`, `MatSmtpClient`) in step with them.

## Map

```
src/MatOS.Core        domain, no web types
  Entities/ Data/            EF entities (AuditableEntity, soft delete), DbContext, migrations
  Apps/                      AppService (install/update/uninstall/lifecycle; redeploys the apps first and the reverse proxy last; a failed install can always be retried or removed),
                             ImagePreflight (the images of a stack pulled before the engine gets it: a refusal stops the install with the reason, a local copy is used), ComposeRenderer (catalog entry -> the one
                             compose file; ComposePorts: a routed app publishes no port, the proxy the ports of its port routes),
                             ComposeFiles, ComposeRunner, AppReconciler (adopt MatDock stacks), AppVariables, PortPlanner, AppAddresses (public and internal address of an app)
  Engines/                   IStackEngine: LocalComposeEngine (own docker compose) and MatDockEngine; StackEngines picks one;
                             EngineClient/EngineLocator (address + API key of an installed engine), MatDockClient, EngineIds, AppService.StartPathAsync (an app bound to MatIdentity is opened at its sign-in, the catalog's `ui.signIn`, so the person lands in it without a click), RuntimeReader (what the pages show of the runtime: asked from MatDock; the Task Manager `/Tasks` is its list of every container with CPU and memory)
  Backups/                   IBackupProvider: LocalBackupProvider (BackupService, own runner) and MatDockBackupProvider; BackupHub merges
                             them (ids local:<id>, matdock:<id>); BackupSchedulerService runs the local schedules
  Catalog/                   CatalogParser/Service/Models + apps/*.yml (embedded)
  Docker/                    DockerCli (process wrapper), DockerService (typed ops, parsing)
  Integrations/              MatcadClient, MatddnsClient, ProxyPublisher (routes, wildcard unless one certificate per app), DnsCheck (name servers: does an own domain exist, where a host points), DnsPublisher (records; DnsTarget: the public IP, a fixed IP (Configuration/DnsIp,
                             the question "fixed IP?") or the tunnel; switching replaces the source, rules before records), IAppProvisioner (one per engine),
                             MatIdentityClient + IdentityLink (MatOS's own sign-in, the issuer of MatIdentity) + SelfBrand (that sign-in in the look of MatOS, no word of MatIdentity: docs/ARCHITECTURE.md "MatOS's sign-in looks like MatOS"), MatIdentityPeople (SCIM to MatIdentity: the People area) + PersonAccess (what a person may use, kept a minute), MatSmtpClient + MailLink (the setup of MatSMTP), FrameLink (MatOS's
                             shell.embed: MAT_FRAME_ANCESTORS), RouteFrameHeaders + FrameHeaders (every app in the window: an app without MAT_FRAME_ANCESTORS and without a CSP of its own gets frame-ancestors naming MatOS on its Matcad route, docs/ARCHITECTURE.md "Every app in the window"), PlatformClient (/api of an app, contract 2),
                             BindingBroker (the bindings between the apps and of the engines: identity.oidc, mail.smtp, web.route (a host under the domain, else a port of
                             this machine: every app behind Matcad; PortsAsync); one class for every capability, AppBinding;
                             catalog consumes + /api + implicit web.route; AdoptAsync takes over the variables of an earlier MatOS), AppManifests + ManifestCache (definitions and values, short
                             timeouts, kept a while), ShellRules (who sees a contribution: `audience`, and `roles` by the person's role in the app; which addresses it may open);
                             the free address (docs/SERVICES.md, docs/ARCHITECTURE.md "The free address"): ServicesClient (the address service, short timeouts, `Unreachable` instead of
                             exceptions), HostedAddressService (register, use, the mode switch with Matddns's DynDns group, give back; SyncAsync in ConnectEngines),
                             TunnelClient (hosted service: the control WebSocket, the streams to Caddy via TunnelTarget) + TunnelProtocol (messages, back-off) + TunnelPipe (the bytes both ways);
                             reached from anywhere (docs/ARCHITECTURE.md "Reached from anywhere"): Reachability (the decision, pure), RouteService (the port check of MatOS Services,
                             the route: direct or the tunnel, also for an own domain, the same addresses either way; SyncAsync in ConnectEngines) + ReachWatcher (the daily check, as a job)
  Jobs/                      JobService/Queue/RunnerService, AppJobs (the work per job kind), JobContext/log
  Setup/                     the first start (docs/ARCHITECTURE.md "The first start"): SetupPlan (plain names of the engines, the engines that follow from the answers, the needs, the records to create, the steps in order), SetupService
                             ("Set up MatOS" in two phases: BeginAsync, ContinueAsync, the test, Try again with the dependents), SetupFlow (which screen is next, DesktopUrl /?start=1), SetupRun/SetupProgress (the lines in SystemConfig.Setup, the progress screen),
                             SetupTest (the rows of the test), AccountView, MailSetup (MatSMTP for the screen of the mail), GettingStarted + GettingStartedService (the list /Start: its rows and when they are done), DockerAdvice
                             (the check of Docker in plain words), AccountRules (e-mail as user name, MatIdentity's password rules); Integrations/OwnerHandover gives MatIdentity the same account
  Auth/ Users/ Security/     login, sessions (EndIdentitySessionsAsync: the back-channel logout), LogoutToken (the check of a logout token of MatIdentity), PBKDF2 hasher, secret protector; Wallpapers (the pictures of the home screen); HomeLayout (how a person arranged the home screen: pages, what is off it, sizes; User.HomeLayout)
  Configuration/             options (MatOSConfiguration: the sections MatOS and Mattiverse), AppPaths, JsonConfigStore, SystemConfig; RenameUpgrade (the first start of an install of Mattiverse: its database, its names), RenameFollowUp (the job after it: the marks in MatDock and Matcad, the own route, the frame addresses), Mattiverse (the old names)
src/MatOS.Web         Razor Pages, controls, JS/CSS, Program.cs
  Controls/                  tag helpers icon (a use of the sprite), app-icon, mat-field (label, control, hint, error), tab-bar; ListModels (ToolbarModel, PagerModel, ListView, ListQuery)
  Pages/                     Index (MatOS, with the tour of the first visit), Setup/ (the first start: Index = Welcome + account, Engines, Choose, Progress, Account, Mail; Core/Setup/SetupFlow says which one is next), Start/ (the list Getting started, in a window at the end of the first start), Engines/ (one engine in the window), Apps/, Store/, Backups/, Jobs/, Settings/, Wizard/ (changes later), People/ (the people, groups and grants of MatIdentity:
                             Index, Edit, Access, Groups, Group), Users/ (MatOS's own accounts, a tab of People), Account/, Share/
  Pages/Shared/              _Layout (the shell: menus, switcher, user, theme, breadcrumb), _Launcher (MatOS: status bar, wallpaper, the windows, no menu), _Onboarding (the first start: wallpaper, a card of glass), _Bare (no menu), _Head, _Toolbar, _Pager, _ValidationSummary, _StatusPill, _DnsFields (with the question "fixed IP?"), _Reach (reached from anywhere: one status, Check again; Support/ReachView)
  Infrastructure/            cookie session validation, SessionCookie (writes the cookie again and keeps its id token and persistence), setup guard, DB bootstrap, API endpoints (the job log, the theme, PUT/DELETE /api/home: the arrangement of the home screen), AddressEndpoints (GET /api/address/name: the live check of a free address, open during the first-run setup), OnboardingEndpoints (GET /api/setup/progress, POST /api/home/tour), SetupEndpoints (GET /api/setup/ip, POST /api/setup/test: the test of the choices; GET /api/setup/mail, POST /api/setup/mail/test), ReachEndpoints (GET /api/reach, POST /api/reach/check: queues the check), health check, MatIdentitySignIn, BackchannelLogout (POST /signout-backchannel)
  Support/                   Toolkit (the icon sprite), Ui (Icon, Logo), Navigation (the menus: NavSet, NavItem, NavBadge), Launcher (the parts of MatOS as apps on the home screen and in its dock), EngineOverview (the five engines for the settings, the home screen and /Engines), HomeContributions (the launchers, quick actions and widgets of the apps for the home screen), Crumbs, AppUrls, FrameProbe, AppStatus, Format, SetupRequests (the request side of Core/Setup/SetupFlow: localhost to 127.0.0.1)
  wwwroot/matui/             the toolkit, a copy of MatDesign's; wwwroot/css/matos.css the few classes only this app has (mv-*, mv-in-window); css/os.css the home screen (os-*); css/onboarding.css the first start and the tour (ob-*, tokens only); js/matos.js its behaviour, js/os.js the home screen, js/onboarding.js the first start and the tour, js/window-mode.js a page in the window
src/MatOS.Services         MatOS Services: names and keys (Instances/), the tunnel (Tunnel/), the edge that passes TLS by SNI and HTTP by Host without decrypting (Edge/),
                             the DNS of the zone (Dns/), DynDNS2 for Matddns, the status and admin pages
tests/MatOS.Tests       unit tests + WebUiTests (they read the source of the pages)
tests/MatOS.Services.Tests its unit tests and in-process end-to-end tests (free ports; the DNS binds TCP first, then UDP on the same number)
tests/e2e                    run.js: the pages in a real browser against the running dev stack; run.js --first-start: the first start on a MatOS without an account
```

## Conventions

**Code**
- Microsoft C# guidelines, file-scoped namespaces, `.editorconfig`. Few nested types; several business functions per class are fine.
- Long Docker work never runs in a request: queue a `Job` (`AppJobs.*`) and let the pages poll `/api/jobs/<id>/log`.
- **Read and change through the engines.** What a page shows of the runtime (status, containers, logs, the host) comes from MatDock through `Engines/RuntimeReader`
  (Docker directly only before MatDock is there, while it does not answer, and inside a job); installs, updates, backups, routes and
  DNS records go through the engine's API with `managedBy: matos` (`Core/Owner.cs`; `mattiverse`, the mark of an install of Mattiverse, is its own too: docs/ARCHITECTURE.md "Renamed from Mattiverse"). Never write to an engine without that marker; never delete what MatOS did not create.
- Engine clients share the request's `DbContext` (the locator reads the app): never call two of them in parallel.
- Anything that parses CLI output has a unit test (a stopped container has an empty last field: never `Trim` such lines).
- Generated files use LF. A single quote must not appear in variable values (they are written single-quoted to `.env`).

**Database**
- JSON for configuration (`config/system.json`), SQLite for logic. Tables are PascalCase and singular; the key is `Id` (64 bit);
  rows have `CreateDate`, `CreateUserId`, `UpdateDate`, `UpdateUserId`, `UpdateState` (0 deleted, 1 created, 2 updated);
  security keys are a `Token` UUID (`UserSession`, `ShareLink`). New schema: `dotnet ef migrations add <Name> --project src/MatOS.Core
  --startup-project src/MatOS.Web --output-dir Data/Migrations`.

**UI** (the rules are MatDesign's: the rulebook and the Components page; where this list and the rulebook differ, the rulebook wins)
- A page is built only from the classes of the toolkit (`mat-*`); what only MatOS has is `mv-*` in `wwwroot/css/matos.css` (tokens only, no literal colour).
  **No inline style, no inline script, no event-handler attribute** (`WebUiTests` checks).
- CRUD = a list plus a separate page for create/edit. A list: the toolbar on top (`<partial name="_Toolbar" />`: a search field, Filter and Sort as dialogs; the page model builds it from `ToolbarModel`
  and `ListView`; the query is `q`, the filters by name, `sort`, `dir` and `page`), the table (`mat-table`, a `data-label` on every cell, a status as `mat-pill`), `_Pager` directly under it, the actions below
  (`mat-actions`, left-aligned). An empty list says so (`mat-empty`) and offers the action that fills it.
- A form: `<form class="mat-form" novalidate>`, `_ValidationSummary` on top, fields as `<mat-field>`, then `mat-btn-row`: Save, Back, (space) Delete. The browser-side messages come from `data-msg-*`, the server's
  land in the same line. A save redirects and sets `StatusMessage` (TempData): the layout shows it as a toast, or as an alert when `IsError`. Confirmations are `data-confirm`. Dependent fields: `data-show-if`.
- The menu is `Navigation.For` (two menus for an administrator, one for a user; Settings is a submenu; a badge counts what runs). A page sets `Title`, `PageSub`, `Crumbs` and, where needed, `Width`, `OwnHead`
  or `Flush` (an app in a frame). Pages without a menu (sign-in, setup, a shared link, errors) use `_Bare`.
- **The logo is the planet with its orbit (`img/logo.svg`), on purpose:** the owner prefers it to the flat glyph tile that rule IDENT-03 asks for. Do not replace it without asking.
- **The home screen is MatOS, on purpose** (owner, 2026-11-08/09; docs/ARCHITECTURE.md "The home screen"): `_Launcher` with `css/os.css` and `js/os.js`, its own look
  (status bar, glass, the system font; colours only as `--os-*` in `os.css`), NOT the toolkit: do not bring the menu of MatUi back there. Windows like a desktop (`js/os.js`): `data-window` opens a page in a window of its own (an iframe from the template in `_Launcher`); the bar moves it, the edges size it, the top and the sides of the
  screen take it full or as a half, the dock is the taskbar, the place of an app's window is kept in `localStorage`, the open windows of a tab in `sessionStorage`. The home screen never reloads by
  itself (it would lose the windows): it refreshes in place (`refresh()`). `js/window-mode.js` drops the menu, header and breadcrumb of a page in there (`mv-in-window`, `mv-window-only`), `_Bare` pages (`data-top-level`) leave it. A link with `data-window` in a page in there asks the home screen for a window of its own (the message `mv-open`, only from its own origin and frames, a path of this MatOS only), and `/?start=1` (the end of the first start) opens *Getting started* (`/Start`) in a window.
  In there a page also looks like a part of the window (`matos.css`, "The look of a page in there"): the title of the layout is hidden (the bar names the page), tabs are a segmented control, and the tabs in `mv-winnav` (settings, account) are the sidebar of a wide window.
  A page reached only through the menu needs a way inside the window (the settings have `_SettingsTabs`). Installed apps open in the window too (the address Matcad serves; *Always open in a new tab* in the menu of an app); the engines
  live in the folder *System* and open `/Engines?id=` (`Support/EngineOverview.cs`); their own pages open there in the window when they allow MatOS to frame them
  (`MAT_FRAME_ANCESTORS`, filled by `Core/Integrations/FrameLink.cs` for apps whose compose file names it; `FrameProbe`/`FramePolicy` check before a frame). Wallpapers are drawn by `scripts/wallpapers` (`node scripts/wallpapers/render.js`);
  a new one needs its id in `Wallpapers.All`, its rule in `os.css` and its pictures (`WallpaperTests` checks all three).
  It never scrolls: what does not fit on the screen continues on the next page (`flow()` in `js/os.js`). The person arranges it and the account keeps it (`HomeLayout`, `/api/home`): pages, dragging, a right click on everything, editing with a minus; every thing of the home
  screen needs a stable key (`data-home-key`, `IndexModel.ArrangeAsync`), and a new widget offers its sizes (docs/ARCHITECTURE.md "The home screen").
- The theme: the toolkit's button cycles System / Light / Dark and posts the choice to `/api/theme`; the account keeps it. Account, logout and the theme are at the bottom of the menu.
- Mobile matters: check phone width (390 px) for every new page; secondary table columns get `mat-hide-sm`.
- After a change to a CSS/cshtml file the container must be rebuilt (they are baked into the image).
- Take screenshots of real data only; never real user data in `docs/images`. The pictures of the README come from a second MatOS (own volume, port 8130, own subnets) with two apps installed
  through the UI; remove it afterwards. With `MatOS__ProjectPrefix` and `MatOS__NetworkName` of its own (`mvshots`) it installs its own engines beside the dev stack's: catalog copies in
  `/data/catalog` with a subnet each, Matcad's proxy on other ports than 80/443, MatIdentity and MatSMTP from local images (`pull_policy: never`; their GHCR images are private).
  Give it a port range of its own too (`MatOS__HostPortRangeStart/End`, the dev stack's Matcad holds 8100 and up) and reach it as `http://127.0.0.1:8130` (`localhost` stops answering once the container has joined its network). The window pictures (`windows.png`) place their windows through `localStorage` (`matos.os.geo.<app>`).
  Shoot with Edge started with `--lang=en-GB` (the clock and the dates follow the browser's language, not Playwright's `locale`) and a fixed clock (`page.clock.setFixedTime`).

**Git**
- Commit and push **under the user's name**, never add a Claude/Anthropic co-author trailer or "generated with" line.
- Branches: `main` = release, `dev` = development. Versions: release `<major>.<minor>.<build>-<date>`, development
  `nightly-<build>-<date>`, local `local-<date>` (`Directory.Build.props`, `.github/workflows/build.yml`).
