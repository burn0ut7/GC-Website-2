CREATE TABLE gc.mission_launch_requests (
    request_id uuid PRIMARY KEY,
    server_id uuid NOT NULL,
    catalog_id uuid NOT NULL REFERENCES gc.catalog_missions(id),
    requester_identity text NOT NULL,
    requested_at timestamptz NOT NULL DEFAULT now(),
    status text NOT NULL DEFAULT 'queued' CHECK (status IN ('queued','cancelled','running','completed','failed')),
    manifest jsonb NOT NULL
);
CREATE UNIQUE INDEX mission_launch_one_queued ON gc.mission_launch_requests(server_id) WHERE status='queued';
