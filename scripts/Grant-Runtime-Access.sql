-- Run after migrations as the database/schema owner in the GC database.
-- Create gc_web and gc_worker LOGIN roles with independently managed passwords first.
-- Neither runtime account owns tables or gets permission to run migrations.
GRANT USAGE ON SCHEMA gc TO gc_web, gc_worker;
GRANT SELECT ON gc.schema_migrations TO gc_web, gc_worker;
GRANT SELECT, INSERT, UPDATE ON gc.users, gc.external_identities TO gc_web;
GRANT SELECT, INSERT, UPDATE ON gc.account_preferences TO gc_web;
GRANT SELECT, INSERT ON gc.ingest_receipts TO gc_web;
GRANT SELECT ON gc.telemetry_batches TO gc_web;
GRANT SELECT ON gc.mission_batches,gc.mission_players,gc.session_events,gc.delivery_failures TO gc_web;
GRANT SELECT, INSERT, UPDATE ON gc.ingest_receipts TO gc_worker;
GRANT SELECT, INSERT ON gc.mission_batches,gc.session_events TO gc_worker;
GRANT SELECT, INSERT, DELETE ON gc.mission_players TO gc_worker;
GRANT SELECT, INSERT, UPDATE, DELETE ON gc.delivery_failures TO gc_worker;
GRANT SELECT, INSERT ON gc.telemetry_batches TO gc_worker;
GRANT SELECT, INSERT, UPDATE ON gc.worker_heartbeat TO gc_worker;
GRANT USAGE ON SCHEMA gc_jobs TO gc_worker;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA gc_jobs TO gc_worker;
GRANT SELECT ON gc.worker_heartbeat TO gc_web;

-- Developer-only data explorer removal; all predicates and confirmation are enforced by the web routes.
GRANT DELETE ON gc.ingest_receipts,gc.telemetry_batches,gc.mission_batches,gc.mission_players,gc.session_events TO gc_web;
GRANT SELECT ON gc.catalog_sources,gc.catalog_missions,gc.catalog_sessions TO gc_web,gc_worker;
GRANT INSERT,UPDATE ON gc.catalog_sources,gc.catalog_missions TO gc_worker;
GRANT UPDATE ON gc.mission_players TO gc_worker;

GRANT SELECT,INSERT,DELETE ON gc.steam_link_challenges TO gc_web;
GRANT DELETE ON gc.external_identities TO gc_web;
GRANT SELECT ON gc.game_identity_observations,gc.verified_game_identities TO gc_web;
GRANT SELECT,INSERT,UPDATE ON gc.game_identity_observations TO gc_worker;

GRANT SELECT,INSERT,UPDATE,DELETE ON gc.patreon_connections,gc.patreon_link_challenges TO gc_web;
