# In-game admin mission requests

Apply database migrations through the existing `--migrate` workflow, then reapply `scripts/Grant-Runtime-Access.sql` before using the new routes. Migration 012 adds `gc.mission_launch_requests`; migration 014 adds worker claims and outcomes. The runtime web role needs select/insert/update on that queue and column-scoped update permission on catalogue availability for PostgreSQL's shared row lock. The process worker is a separate GC Tools product, not bundled into the website.

The `/api/v1/catalog/selection` routes use the existing Ingest machine credential and its configured server identity. Website roles do not authorize them. Credentials remain private configuration, never browser scripts or game clients.

- `POST /query`: `{ "search": "", "offset": 0 }`. Searches names, tags and minimum/maximum player counts; returns 25 rows and `nextOffset` (-1 at the end). Includes unavailable and unlisted catalogue entries for admin inspection.
- `GET /{missionId}`: full description, tags, thumbnail resource and declared add-on IDs.
- `POST /launch`: `{ "requestId": "UUID", "missionId": "UUID", "requesterIdentity": "game identity" }`. The authenticated game server supplies the requester after checking native admin login. Validates current availability and scenario identity, snapshots the launch metadata, and returns `status: queued`.

Responses use `{ "version": 1, "chunks": ["..."] }`: concatenate the strings to obtain JSON. Each chunk is bounded for game RPC transport and preserves Unicode pairs. Neither description nor tag text is silently truncated.

Launch request IDs are idempotent and bound to the server, mission and requester. Concurrent queue updates serialize per server. A new request cancels that server's previous **queued** request only. Already claimed/running requests are not cancelled. A retry returns the original status; identity conflicts return 409. Network failure is not evidence that nothing was queued.

The immutable manifest records the scenario resource, normalized `requiredAddonIds`, source metadata, dependency coverage and `execution: pending-worker`. Legacy data retains `declared-only` coverage. The external worker resolves the owning addon, dependency closure and versions, validates its trusted deployment, and acknowledges execution. Do not treat partial authoring evidence as exhaustive or fall back to all installed mods. No commands or machine paths are accepted from game clients.

Validation: `dotnet build tests/GC.Infrastructure.Tests/GC.Infrastructure.Tests.csproj -o artifacts/voting-tests --no-restore`, then the `--catalog` suite with a process-scoped local PostgreSQL test-admin connection. Tests use disposable databases. The existing `--voting-live` fixture also serves these routes for end-to-end MCP checks and restores the private profile configuration on exit.

## Dedicated worker handoff

Mission selection now snapshots the scenario, normalized required addon GUIDs,
source repository and catalog source fingerprint, plus dependency coverage, into the durable request.
Schema-2 authoring evidence maps nested-resource addon IDs through its loaded-addon
GUID table. The importer unions those GUIDs with explicit requirements; it does
not include every addon that happened to be loaded in the editor. Coverage remains
`authored_container_graph_partial`, not a certification of dynamic dependencies.

The existing server-scoped Ingest credential authorizes these machine endpoints:

- `POST /api/v1/catalog/launch-worker/claim`, body `{ "claimId": "<uuid>" }`:
  returns an assignment, 204 when no request is queued, or 409 when another claim
  owns a running request. Repeating a claim returns its original assignment.
- `POST /api/v1/catalog/launch-worker/<requestId>/result`, body containing
  `claimId`, `status` (`completed` or `failed`), `summary`, `planSha256` and
  `processId`: records a server/claim-bound outcome. Success requires a plan hash
  and process ID. An identical retry is accepted; contradictory outcomes are not.

Claims do not expire automatically. The external server worker must retain its
claim before making the request and reconcile ambiguous process ownership after
a crash. A new selection cancels queued requests but never steals a running one.
The website records the worker's outcome; it does not inspect game processes or
certify engine readiness itself. Website roles do not authorize these routes.

`tests/GC.Infrastructure.Tests --launch-live <header.conf> <private-output-dir>`
is an explicit local experiment: it imports that real saved header into a fresh
disposable database, queues through the authenticated production HTTP route and
serves the claim/result endpoints. `fixture.json` contains a private test key.
Create `stop.txt` in its output directory to stop and drop the test database.

