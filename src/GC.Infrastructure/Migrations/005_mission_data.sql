ALTER TABLE gc.ingest_receipts DROP CONSTRAINT ingest_receipts_byte_count_check;
ALTER TABLE gc.ingest_receipts ADD CONSTRAINT ingest_receipts_byte_count_check
    CHECK (byte_count > 0 AND byte_count <= CASE WHEN stream='telemetry' THEN 1048576 ELSE 67108864 END);
CREATE INDEX intake_pending_missions ON gc.ingest_receipts(next_attempt_at,received_at)
    WHERE stream IN ('stats','session','history') AND status='received_unvalidated';
CREATE INDEX intake_recent ON gc.ingest_receipts(received_at DESC);

-- Small metadata and queryable records; full source bytes remain in the private archive.
CREATE TABLE gc.mission_batches (
    server_id uuid NOT NULL, mission_id uuid NOT NULL, stream text NOT NULL CHECK (stream IN ('stats','session','history')),
    revision integer NOT NULL CHECK (revision>=0), header text NOT NULL, world text NOT NULL,
    started_at bigint NOT NULL, finished boolean NOT NULL, source_complete boolean NOT NULL,
    dropped_records integer NOT NULL, first_sequence integer, last_sequence integer,
    first_seconds double precision, last_seconds double precision,
    PRIMARY KEY(server_id,mission_id,stream,revision)
);
CREATE TABLE gc.mission_players (
    server_id uuid NOT NULL, mission_id uuid NOT NULL, revision integer NOT NULL,
    identity text NOT NULL, name text NOT NULL, counters text NOT NULL,
    PRIMARY KEY(server_id,mission_id,identity)
);
CREATE TABLE gc.session_events (
    server_id uuid NOT NULL, mission_id uuid NOT NULL, sequence integer NOT NULL,
    session_seconds double precision NOT NULL, event_type text NOT NULL,
    actor text NOT NULL, target text NOT NULL, detail text NOT NULL,
    PRIMARY KEY(server_id,mission_id,sequence)
);
CREATE INDEX session_event_type ON gc.session_events(server_id,mission_id,event_type,sequence);

ALTER TABLE gc.telemetry_batches ADD COLUMN mission_id uuid;
UPDATE gc.telemetry_batches SET mission_id=(header::json->>'missionId')::uuid;
ALTER TABLE gc.telemetry_batches ALTER COLUMN mission_id SET NOT NULL;
CREATE INDEX telemetry_mission ON gc.telemetry_batches(server_id,mission_id,recording_id);

-- A failed source file stays available for repair; backoff prevents one poison file blocking others.
CREATE TABLE gc.delivery_failures (
    server_id uuid NOT NULL, stream text NOT NULL, filename text NOT NULL,
    attempts integer NOT NULL, error_code text NOT NULL, next_attempt_at timestamptz NOT NULL,
    PRIMARY KEY(server_id,stream,filename)
);
