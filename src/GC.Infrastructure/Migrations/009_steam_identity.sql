CREATE TABLE gc.steam_link_challenges (
    state_hash text PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES gc.users(id),
    expires_at timestamptz NOT NULL
);
-- Observations remain after unlinking. Conflicts never silently reassign history.
CREATE TABLE gc.game_identity_observations (
    identity uuid NOT NULL,
    steam_id text NOT NULL CHECK (steam_id ~ '^[0-9]{17}$'),
    server_id uuid NOT NULL,
    mission_id uuid NOT NULL,
    first_seen_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(identity,steam_id,server_id)
);
CREATE INDEX game_identity_steam ON gc.game_identity_observations(steam_id);
CREATE VIEW gc.verified_game_identities AS
SELECT DISTINCT o.identity,o.steam_id FROM gc.game_identity_observations o
WHERE NOT EXISTS (SELECT 1 FROM gc.game_identity_observations x WHERE x.identity=o.identity AND x.steam_id<>o.steam_id)
  AND NOT EXISTS (SELECT 1 FROM gc.game_identity_observations x WHERE x.steam_id=o.steam_id AND x.identity<>o.identity);
