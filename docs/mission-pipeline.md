# Four-stream mission pipeline

The website worker owns private delivery and projection; the public website never reads a game profile. Lobby2 publishes closed files followed by literal `.ready` markers. The worker archives the original bytes and commits a PostgreSQL receipt before removing a marker. It retains every source payload. The game holds no SQL credentials and clients make no integration calls.

## Enable on a test profile

Apply migrations through 005 with the schema owner, then apply `scripts/Grant-Runtime-Access.sql`. Use separate `gc_web` and `gc_worker` runtime logins. The worker needs:

- `ConnectionStrings__Gc`: worker connection string.
- `Delivery__Enabled=true`, `Missions__Enabled=true`, `Telemetry__Enabled=true`.
- `Delivery__ProfilePath`: absolute private server profile directory.
- `Ingest__ArchivePath`: absolute private archive directory, outside all public roots.
- `Ingest__ServerId`: the UUID already in `Lobby2History/server-id.txt`.

Run the published GC.Worker application. No HTTP credential is needed for same-host delivery; it calls the same intake implementation directly. REST intake remains available for a separate trusted server. All flags default off. Persisted jobs also check their enable flags at execution.

Restrict profile/archive access to the game and worker service accounts. The web service needs archive read access for telemetry; REST intake additionally needs archive write access. Do not grant browser users file access. Directory links are unsupported. Configure reverse-proxy request limits for the documented body limits if REST ingestion is enabled.

## Records and reads

All four canonical contracts live in Lobby2's `integrations/Contracts`; the website consumes its immutable package. Statistics/session/history JSON is bounded at 64 MiB per batch; telemetry stays at 1 MiB. The larger bound accommodates cumulative statistics rather than forcing high-frequency telemetry limits onto them.

| Stream | Indexed in this slice | Original bytes |
| --- | --- | --- |
| Statistics | Latest cumulative player counters, revision and completion | Retained, including assignments, roles, ammo, population and deaths |
| Session events | Sequence, time, type, actor/target and event detail | Retained |
| Mission history | Metadata, phases, outcomes, sampled performance | Retained, including commander intervals |
| Telemetry | Recording/mission link and bounded replay record access | Retained JSON or native L2TB |

Counters use the latest snapshot, never a sum of revisions. Missing optional values stay absent. Data not yet indexed is retained, not represented as zero. Session and statistics clocks have different origins. Sampled FPS is not continuous frame timing.

Machine-authenticated routes use the existing server-scoped bearer credential:

- `GET /api/v1/missions?after=<mission UUID>`: 50 mission IDs per page.
- `GET /api/v1/missions/{mission}`: stream status, coverage, event totals, performance and recording IDs.
- `GET /api/v1/missions/{mission}/players?after=<identity>`: 50 player snapshots per page.
- `GET /api/v1/missions/{mission}/events?afterSequence=0&type=joined`: up to 100 events and approximately 2 MiB; an individual oversized additive event returns 422 instead of skipping it.
- Existing `/api/v1/recordings/{recording}` and `/records` supply telemetry summaries/windows.

Approved Discord administrators have equivalent mission reads at `/api/admin/missions/{server}` and its mission subpaths. This does not broaden ordinary member access. OAuth still needs local configuration. No public statistics or playback UI is added.

Missing streams, gaps, drops, unclosed streams and rejected receipts remain explicit. Mission lists contain projected missions; pending-only missions are not listed yet. A summary can return pending receipts for a known mission ID. Up to 50 recording IDs are returned with `moreRecordings`; a full recording-list endpoint is future work. Restart pagination if live data changes.

## Failure behavior

Delivery attempts eight files per stream per poll, with persistent retry delays for bad/unavailable sources. Invalid files keep their ready marker and a `gc.delivery_failures` record. Unpublished files are ignored. Lost acknowledgement repeats safely; changed bytes at an existing revision conflict. An archive file left before a receipt commit can be reused on retry. Database failure leaves source markers unacknowledged.

Projection uses receipt locks and per-mission serialization. Session event conflicts roll back the whole batch. Out-of-order revisions are accepted without replacing a newer player snapshot. Receipts distinguish accepted/projected/rejected/failed; acknowledgement means durable receipt, not valid projection. Storage read failures retry five times before becoming failed; operator repair/requeue is not yet exposed as a public action.

## Cutover and limits

This is an opt-in foundation, not a production cutover. Do not run the old StatsIngest consumer and GC delivery against the same output folders. Their common lock prevents simultaneous file access, but whichever consumer acknowledges first determines which database receives the file.

The older `--serve-profile` importer also answers Lobby2's player-history mailbox, used by the new-player indicator. GC.Worker does not replace that reader yet. Keep the existing live service unchanged. Before cutover, migrate historical data and port that mailbox query, preserving unknown/incomplete history semantics. Never call an empty new database authoritative zero history. Already acknowledged historical files require a deliberate backfill; this worker only consumes ready files.

Source files and archives currently grow without retention or total-disk quotas. Establish monitored disk budgets, backups and retention before continuous production ingestion. High-player-count throughput, deployment permissions/TLS and dedicated-server load remain unproven. Automated database tests currently use an isolated administrator-owned test database, not production service identities.

## Future website-to-server work

Use a typed, server-owned command mailbox or authenticated server polling. Bind commands to server and mission, with an ID, expiry, allowed action, version and acknowledgement. Game-server code revalidates authority and current mission state before acting; retries are idempotent. Browser requests terminate at the website. No game clients receive web credentials or make these calls. Do not add arbitrary script execution, direct browser-to-game control, or a general command bus before a concrete use case warrants it. Mission voting and new commands are outside this slice.

## Evidence

`Test-Backend.ps1` creates and drops only a random test database. Synthetic tests exercise all four streams, worker restart, a delayed revision, duplicate acknowledgement, storage failure/recovery, orphan publication recovery, conflicting events, bounded reads and authorization. Existing migration, OAuth and telemetry checks run alongside them.

A controlled Workbench session on 2026-11-02 passed through lobby, slotting, briefing, game and production mission end. Its private copied outputs contained 50 batches: 4 statistics, 8 session, 14 history, 24 native telemetry. End-to-end comparison verified 10 session events, 27 telemetry records, latest player counters and sampled performance against source files. This is a small functional capture, not a high-load or visual playback proof. Originals and ready markers were preserved. No private captures are committed.
