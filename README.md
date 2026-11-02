# Global Conflicts backend foundation

.NET 10 website and separate Windows-compatible worker. The existing landing
page is accompanied by an account page and a private read-only data inspector.
No production services are installed automatically.

## Run the website

Install the .NET 10 SDK, then run from this directory:

```powershell
dotnet run --project GC.Web.csproj
```

Open http://127.0.0.1:5080/#home, or double-click `Launch.cmd` and open the
address it prints. Ctrl+C stops the server. The default listener is loopback-only.
The website works without a database; dependent APIs report unavailable.

The introduction video never starts automatically, including after revealing
the embed. Press YouTube's play button to start it; initial volume is 25%.

Sessions are anchored to Saturday/Sunday at 21:00 Europe/Berlin (Central
European civil time), based on the community's normal 3 p.m. Eastern start and
temporary 4 p.m. start during the Europe/US clock-change gap. This is 19:00 UTC
in European summer and 20:00 UTC in winter. `dist/schedule.js` converts the next
sessions to the visitor's timezone, including local weekday changes, and falls
back to UTC when no usable local timezone is detected. Run
`node tests/schedule.test.cjs` to check DST boundaries and date rollover.

## Current capabilities

| Component | Implemented starting point |
| --- | --- |
| Website | Static landing assets plus versionable REST endpoints |
| Database | PostgreSQL/Npgsql, explicit transactional migrations, checksum ledger, readiness |
| Worker | Separate process/Windows Service support, persistent Quartz scheduler, heartbeat and telemetry projection jobs |
| Accounts | GC UUIDs and unique provider identities; concurrent Discord sign-ins converge on one account |
| Discord login | Authorization code + PKCE, secure session cookie, identify scope, no stored provider tokens |
| API protection | Anonymous account requests get 401; logout requires authentication and CSRF token |
| Telemetry intake | Server-scoped bearer credential, bounded body, immutable private archive and durable receipt |
| Recording data | Shared Lobby2 contract validation, completeness summary and bounded time-window read API |
| Operations | Process liveness, database readiness, limited request logs, repeatable integration checks |

Discord and intake are disabled by default. Live Discord authorization has not
been exercised: it needs a registered application and HTTPS callback. PostgreSQL
and worker checks were exercised against isolated test databases, not production.

## PostgreSQL and migrations

Use a dedicated GC database. Do not point migrations at the Lobby2 telemetry
proof database. Set `ConnectionStrings__Gc` in the process environment using a
connection string from your secret store (never commit it). For production use
separate owner/migrator, web and worker database roles.

Run migrations explicitly with the owner/migrator credential:

```powershell
dotnet run --project GC.Web.csproj -- --migrate
```

The command exits after migration; normal website/worker startup does not apply
schema changes. Concurrent migrations serialize, repeated migrations do nothing,
and changed or unknown migration versions are rejected. Do not edit an applied
migration: add the next numbered SQL file. Scripts are embedded in the assembly.

After creating `gc_web` and `gc_worker` login roles with independently managed
passwords, apply `scripts/Grant-Runtime-Access.sql` as the owner. Set each process's
`ConnectionStrings__Gc` to its own role. The grant script is repeatable but does
not create roles or save passwords. Review grants when adding tables/jobs.

- `/api/health`: process liveness only, HTTP 200.
- `/api/ready`: schema connectivity/checksums, 200 when ready; otherwise 503.
- Neither endpoint promises Discord availability or a running worker.

## Background worker

After migration, with the worker database connection in its environment:

```powershell
dotnet run --project src/GC.Worker
```

Quartz stores named jobs and triggers in `gc_jobs`. The initial idempotent
heartbeat updates `gc.worker_heartbeat` every 30 seconds. Job concurrency is two;
the job disallows overlap. Cluster coordination permits safe process overlap.
This is the scheduling foundation; no business notifications or arbitrary scripts
are scheduled. The Windows Service adapter is included but no service registration
has been performed. Retention and maintenance jobs come in later small changes.

## Discord login and private inspector

See [Discord identity, old-site review and local setup](docs/discord-identity.md).
`/account.html` shows your linked Discord identity; `/admin` and `/api/admin/*`
require the configured Developer role in the community Discord server.
Membership is checked server-side on every request, not cached in the session.
The inspector reads only this website's database, not the older Lobby2 proof store.

Environment settings required to enable it:

- `Discord__Enabled=true`
- `Discord__ClientId` and `Discord__ClientSecret`
- `Auth__KeysPath`: private persistent directory outside `dist`
- For private inspector access: `Discord__GuildId`, `Discord__DeveloperRoleId`, and server-only `Discord__BotToken`; no individual user allowlist.
- `ConnectionStrings__Gc`
- HTTPS listener/reverse proxy and matching `AllowedHosts`

Register the exact HTTPS callback `/auth/discord/callback` on your domain in
Discord. Visit `/auth/discord` to begin. Successful login returns to the landing
account page. `/api/me` reads the current GC identity. `/api/auth/csrf` returns the token
for cookie-authenticated writes; send it as `X-CSRF-TOKEN` to `POST /auth/logout`.

Sessions expire after eight hours without sliding renewal. Logout removes the
browser cookie; central session revocation and account administration are future
work. Administrator grants currently permit read-only data inspection, not account
administration. Account linking must prove provider ownership; no generic identity-write
endpoint exists. Patreon linking and live bot/webhook handlers are deferred.

Secure cookies require HTTPS. Explicitly configure trusted proxy forwarding at
deployment, never trust arbitrary forwarded headers. Key files must be restricted
to the web service account. On Windows keys use that account's DPAPI protection;
backup/recovery must include a strategy for the service identity and keys. The
same setup has not been production-tested. On other OSes supply protected storage.

## Intake configuration and contract

Required environment settings:

- `Ingest__Enabled=true`
- `Ingest__ServerId`: fixed nonempty UUID identifying the sending game server
- `Ingest__ApiKey`: random secret of at least 32 characters
- `Ingest__ArchivePath`: absolute private directory outside the public web root
- `ConnectionStrings__Gc`

`POST /api/v1/ingest/{stream}/{recordingId}/{revision}` accepts raw file bytes with
`Authorization: Bearer <credential>`. Streams: `stats`, `session`, `history`,
`telemetry`. Revision is a nonnegative integer; recordingId is a nonempty UUID.
The server identity comes from configuration, not caller input. This first adapter
supports one sending server per configured endpoint deployment; multi-server
credential rotation will be a later change. Keep the archive directory controlled
by the service account and do not use junctions/symlinks into public directories.

Bodies are limited to 1 MiB for telemetry and 64 MiB for statistics/session/history, with two concurrent requests. A successful receipt is
HTTP 202 with `accepted` (durable receipt, not a processing result). Same key/content is safely repeatable;
changed content at the same revision returns 409. The archive is flushed before
the database receipt. Interrupted writes can leave orphan files; they are not
acknowledged as imported. No public archive-download route exists.

The optional worker now delivers and projects all four streams. See [the mission pipeline](docs/mission-pipeline.md) for configuration, joined read APIs, proof and production cutover limits. The existing player-history mailbox is not replaced in this slice. See [the telemetry proof](docs/telemetry-proof.md) for the earlier playback-record foundation.

Production intake requires HTTPS; HTTP is permitted only in the explicit
Development environment for local integration tests.

## Validation

Website-only check (no database configured in its process environment):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Smoke-Test.ps1
```

Full backend check: set `GC_TEST_ADMIN_CONNECTION` to an administrator on a local
test PostgreSQL instance, then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Backend.ps1
```

It creates a random `gc_test_*` database and drops only that database in cleanup.
Checks cover concurrent/repeated/drifted migrations, stable concurrent account
creation, readiness, unauthenticated access, disabled Discord, token-protected
intake, duplicate/conflicting/oversized batches, private files and worker restart
without duplicate jobs. Published web and worker processes are stopped afterward.
No live provider calls or original game-file mutations occur. Live OAuth success,
authenticated logout/CSRF and production deployment still need dedicated tests.

## Source and publishing

`dist/` is the single frontend source. Builds copy it into application output;
rebuild/restart after editing. Only those assets are public. Secrets and private
archives must stay outside it. `.local/`, build output and test artifacts are ignored.

```powershell
dotnet publish GC.Web.csproj -c Release -o artifacts/web
dotnet publish src/GC.Worker -c Release -o artifacts/worker
```

Published applications require the matching .NET 10 runtime (ASP.NET Core runtime
for the web app). GitHub is the source repository. `.openai/hosting.json` describes
the earlier static Sites preview; it cannot deploy this backend. No production
IIS/Windows Service registration, TLS configuration or backup service is installed.

Quartz schema migration 002 derives from Quartz.NET v4.6.2, with its destructive
reset removed and tables isolated in `gc_jobs`. See `licenses/Quartz-Apache-2.0.txt`.
Font license information remains in `dist/fonts/`.
