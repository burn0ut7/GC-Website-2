# Lobby2 to website: recording proof

## Decision

Keep the current engine recorder and binary format for this slice. They already
provide recording IDs, ordered events, timestamps, lifecycle events and movement
samples. Adding more game-thread work is not justified before the consumer uses
what is recorded. This is a recording-data proof, not a full mission replay or
128-connected-player performance result.

Extract the existing C# decoder, batch validation and continuity rules into one
canonical Lobby2 package shared by its importer and this website. Preserve raw
bytes in the private archive. Index only batch metadata, time bounds and small
counts in PostgreSQL. Do not inflate all movement samples into database rows or
decode an entire mission to serve a short time window.

## Worker

After applying migration 004 and runtime grants, set `Telemetry__Enabled=true`
and the same absolute private `Ingest__ArchivePath` used by the web intake.
The worker's persistent named job processes at most eight receipts every five
seconds. Disabling the flag also guards execution of an already persisted trigger.

Each batch is processed in one database transaction. Receipt row locks prevent
duplicate workers; a recording-level advisory lock serializes continuity checks.
The worker checks stored file length/hash, decodes and validates the actual Lobby2
contract, verifies envelope ID/revision and neighboring sequence/time boundaries,
then commits the index and receipt status together. A process interruption rolls
back the work and leaves the receipt available for restart.

Statuses: `received_unvalidated`, `projected`, `rejected`, `failed`. Malformed,
conflicting or corrupted data gets a stable error code and does not block later
receipts. Missing/unreadable files retry every 30 seconds up to five attempts,
then fail. Repair/retry administration and archive quotas remain separate work.
Other streams are retained but not projected by this telemetry-specific job.

Header JSON is stored as text, preserving the canonical validator's acceptance
of additional fields without imposing PostgreSQL JSONB's extra string/number
restrictions. Counts use JSONB with only validated event-type keys and integers.
Nothing in the original archive is altered by projection.

## Private read API

Both endpoints use the existing server-scoped intake bearer credential and HTTPS
outside Development. They do not grant all signed-in Discord users access. UI
publication/member permissions will be designed separately. Responses use no-store.

- `GET /api/v1/recordings/{recordingId}`: counts, bounds, duration, metadata,
  receipt statuses and recording completeness. A closure alone is not completeness:
  revisions must start at zero and be contiguous through closure. Drops, explicit
  gaps and rejected/failed receipts are separately reported. Entity count means
  distinct recorded spawn IDs, not concurrent human players.
- `GET /api/v1/recordings/{recordingId}/records?fromMs=...&untilMs=...&afterSequence=0&limit=250`:
  original logical events in inclusive world-clock milliseconds. Maximum window
  60 seconds; limit 1–500; maximum eight bounded archive chunks and approximately
  2 MiB of record JSON per response. Follow `nextAfterSequence` until null, retaining
  the same recording/window. A page can be empty while still providing a cursor.
  Use the summary first; an unknown/unprojected range can return an empty page.

Pagination is for a stable projected recording. If more batches arrive, restart
the query after the summary's validated batch count changes; this first API does
not provide a snapshot token across requests. It returns recorded events only,
not interpolated positions, a seek checkpoint, inferred continuity or terrain.
An unreadable archive returns 503; a failed checksum/size check reports
`archive_integrity` and returns no event data.

## Reproducing validation

The usual `scripts/Test-Backend.ps1` runs an isolated twelve-batch synthetic proof,
including the eight-chunk continuation boundary. To use a real archived capture,
also set `GC_TEST_RECORDING_DIRECTORY` and `GC_TEST_RECORDING_ID` before running it.
The directory must contain that recording's original revision files (`.json` or
`.l2t`), with at least three batches and a normal closure. Use a private copy,
not a live agent inbox. No capture files are checked into Git.

The test uploads reversed revisions while withholding a middle batch, restarts
the worker with pending work, verifies incompleteness, fills the gap, and compares
counts/duration/window records to the source. It also covers duplicates, metadata
and envelope conflicts, malformed files, binary decoding, archive corruption,
extended header metadata, authorization and unchanged source hashes. Data and
private test archive are cleaned up; original source files are never consumed.

Real movement capture evidence: 43 batches, 4,767 records, 129 recorded entity
lifetimes, 154,981.145 ms recorded span; includes movement and vehicle/seat events.
A one-second window returned 35 matching records in three chunk reads with a
deliberately small 17-record page size. It is a Workbench capture with fixture AI,
not 129 connected players, and it does not establish projectile/animation fidelity.

A second proof used private copies of 21 actual engine-written L2TB batches:
885,939 source bytes, 1,892 records, 129 entity lifetimes, 48,450.469 ms recorded
span. Its one-second window returned 33 matching records in two chunk reads.
This was the previously documented mostly stationary fixture capture; it proves
native binary consumption, not representative mission performance. Both real
captures passed the same restart, missing-data and corruption regressions.

## Next proof

Build deterministic seek state from these events: actor creation/removal,
position at a chosen timestamp, vehicle seating and explicit gap behavior. Only
add recorder fields if that exercise identifies a concrete missing fact. Keep
rendering and terrain calibration separate from transport completeness.
