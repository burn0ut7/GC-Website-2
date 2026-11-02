ALTER TABLE gc.ingest_receipts DROP CONSTRAINT ingest_receipts_status_check;
ALTER TABLE gc.ingest_receipts ADD CONSTRAINT ingest_receipts_status_check
    CHECK (status IN ('received_unvalidated', 'projected', 'rejected', 'failed'));
ALTER TABLE gc.ingest_receipts ADD COLUMN attempts integer NOT NULL DEFAULT 0;
ALTER TABLE gc.ingest_receipts ADD COLUMN next_attempt_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE gc.ingest_receipts ADD COLUMN error_code text;
ALTER TABLE gc.ingest_receipts ADD COLUMN processed_at timestamptz;
CREATE INDEX intake_pending_telemetry ON gc.ingest_receipts(next_attempt_at, received_at)
    WHERE stream='telemetry' AND status='received_unvalidated';

CREATE TABLE gc.telemetry_batches (
    server_id uuid NOT NULL,
    recording_id uuid NOT NULL,
    revision integer NOT NULL,
    header text NOT NULL,
    from_ms double precision NOT NULL,
    until_ms double precision NOT NULL,
    record_count integer NOT NULL,
    first_sequence integer NOT NULL,
    closed boolean NOT NULL,
    dropped_records integer NOT NULL,
    type_counts jsonb NOT NULL,
    spawned_entities text[] NOT NULL,
    PRIMARY KEY(server_id, recording_id, revision)
);
CREATE INDEX telemetry_time_range ON gc.telemetry_batches(server_id, recording_id, from_ms, until_ms);
