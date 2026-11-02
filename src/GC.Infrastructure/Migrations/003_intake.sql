CREATE TABLE gc.ingest_receipts (
    server_id uuid NOT NULL,
    stream text NOT NULL CHECK (stream IN ('stats', 'session', 'history', 'telemetry')),
    recording_id uuid NOT NULL,
    revision integer NOT NULL CHECK (revision >= 0),
    sha256 text NOT NULL,
    byte_count integer NOT NULL CHECK (byte_count > 0 AND byte_count <= 1048576),
    archive_name text NOT NULL,
    received_at timestamptz NOT NULL DEFAULT now(),
    status text NOT NULL DEFAULT 'received_unvalidated' CHECK (status = 'received_unvalidated'),
    PRIMARY KEY (server_id, stream, recording_id, revision)
);
