ALTER TABLE gc.external_identities ADD COLUMN avatar_hash text;
CREATE TABLE gc.donation_preferences (
 user_id uuid PRIMARY KEY REFERENCES gc.users(id),
 anonymous boolean NOT NULL DEFAULT false,
 show_amount boolean NOT NULL DEFAULT false
);
CREATE TABLE gc.patreon_campaign_members (
 campaign_id text NOT NULL, subject text NOT NULL,
 monthly_cents numeric(18,4), supporting_since timestamptz,
 PRIMARY KEY(campaign_id,subject), CHECK(monthly_cents IS NULL OR monthly_cents >= 0)
);
CREATE TABLE gc.patreon_campaign_sync (
 campaign_id text PRIMARY KEY, checked_at timestamptz NOT NULL, currency text NOT NULL
);
