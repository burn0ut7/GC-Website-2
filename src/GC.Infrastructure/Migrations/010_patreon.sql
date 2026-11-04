CREATE TABLE gc.patreon_link_challenges (
    state_hash text PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES gc.users(id),
    expires_at timestamptz NOT NULL
);
CREATE TABLE gc.patreon_connections (
    user_id uuid PRIMARY KEY REFERENCES gc.users(id),
    subject text NOT NULL UNIQUE,
    protected_tokens text NOT NULL,
    campaign_id text NOT NULL,
    support_status text NOT NULL,
    checked_at timestamptz NOT NULL DEFAULT now(),
    next_check_at timestamptz NOT NULL DEFAULT now() + interval '15 minutes'
);
