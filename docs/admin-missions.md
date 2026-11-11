# In-game admin mission requests

Apply database migrations through the existing `--migrate` workflow, then reapply `scripts/Grant-Runtime-Access.sql` before using the new routes. Migration 012 adds `gc.mission_launch_requests`. The runtime web role needs select/insert/update on that queue and column-scoped update permission on catalogue availability for PostgreSQL's shared row lock. No restart worker is included.

The `/api/v1/catalog/selection` routes use the existing Ingest machine credential and its configured server identity. Website roles do not authorize them. Credentials remain private configuration, never browser scripts or game clients.

- `POST /query`: `{ "search": "", "offset": 0 }`. Searches names, tags and minimum/maximum player counts; returns 25 rows and `nextOffset` (-1 at the end). Includes unavailable and unlisted catalogue entries for admin inspection.
- `GET /{missionId}`: full description, tags, thumbnail resource and declared add-on IDs.
- `POST /launch`: `{ "requestId": "UUID", "missionId": "UUID", "requesterIdentity": "game identity" }`. The authenticated game server supplies the requester after checking native admin login. Validates current availability and scenario identity, snapshots the launch metadata, and returns `status: queued`.

Responses use `{ "version": 1, "chunks": ["..."] }`: concatenate the strings to obtain JSON. Each chunk is bounded for game RPC transport and preserves Unicode pairs. Neither description nor tag text is silently truncated.

Launch request IDs are idempotent and bound to the server, mission and requester. Concurrent queue updates serialize per server. A new request cancels that server's previous **queued** request only. Already claimed/running requests are not cancelled. A retry returns the original status; identity conflicts return 409. Network failure is not evidence that nothing was queued.

The immutable manifest records scenario resource, normalized declared `requiredAddonIds`, source metadata, `dependencyResolution: declared-only` and `execution: pending-future-restart-worker`. Missing declared IDs stay unknown; display mod names are not converted to IDs. The future PowerShell worker must resolve the owning add-on, dependency closure and versions, validate them, and acknowledge execution. Do not treat this declaration as an exhaustive launch set or fall back to all installed mods. No commands or machine paths are accepted from game clients.

Validation: `dotnet build tests/GC.Infrastructure.Tests/GC.Infrastructure.Tests.csproj -o artifacts/voting-tests --no-restore`, then the `--catalog` suite with a process-scoped local PostgreSQL test-admin connection. Tests use disposable databases. The existing `--voting-live` fixture also serves these routes for end-to-end MCP checks and restores the private profile configuration on exit.
