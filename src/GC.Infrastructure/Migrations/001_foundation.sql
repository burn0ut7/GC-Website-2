CREATE TABLE gc.users (
    id uuid PRIMARY KEY,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE gc.external_identities (
    provider text NOT NULL,
    subject text NOT NULL,
    user_id uuid NOT NULL REFERENCES gc.users(id),
    display_name text NOT NULL,
    linked_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (provider, subject),
    UNIQUE (user_id, provider)
);
CREATE TABLE gc.worker_heartbeat (
    name text PRIMARY KEY,
    last_seen_at timestamptz NOT NULL
);
