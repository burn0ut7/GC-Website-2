ALTER TABLE gc.catalog_missions ADD COLUMN listed boolean NOT NULL DEFAULT true;
CREATE OR REPLACE VIEW gc.catalog_sessions AS
WITH sessions AS (
    SELECT server_id,mission_id,min(started_at) started_at FROM gc.mission_batches GROUP BY server_id,mission_id
    UNION
    SELECT t.server_id,t.mission_id,min((t.header::jsonb->>'startedAt')::bigint)
    FROM gc.telemetry_batches t WHERE NOT EXISTS (
        SELECT 1 FROM gc.mission_batches b WHERE b.server_id=t.server_id AND b.mission_id=t.mission_id)
    GROUP BY t.server_id,t.mission_id
), headers AS (
    SELECT s.*,h.header::jsonb metadata,
        upper(substring(COALESCE(h.header::jsonb->>'scenarioResource',
            CASE WHEN h.header::jsonb->>'scenarioPath' ~* '\.conf$' THEN h.header::jsonb->>'scenarioPath' END),'^\{([0-9a-fA-F]{16})\}')) scenario_guid,
        upper(substring(h.world,'^\{([0-9a-fA-F]{16})\}')) world_guid, h.world world_resource
    FROM sessions s LEFT JOIN LATERAL (
        SELECT header,world FROM gc.mission_batches b WHERE b.server_id=s.server_id AND b.mission_id=s.mission_id AND stream='history'
        ORDER BY revision DESC LIMIT 1
    ) h ON true
)
SELECT h.server_id,h.mission_id,h.started_at,h.metadata,
    CASE WHEN c.matches=1 THEN c.ids[1] END catalog_id,
    CASE WHEN c.matches=1 THEN CASE WHEN h.scenario_guid IS NOT NULL THEN 'scenario-guid' ELSE CASE WHEN h.world_guid IS NOT NULL THEN 'unique-world-guid' ELSE 'exact-world-resource' END END
         WHEN c.matches>1 THEN 'ambiguous' ELSE 'unmatched' END link_status
FROM headers h LEFT JOIN LATERAL (
    SELECT count(*) matches,array_agg(id) ids FROM gc.catalog_missions m
    WHERE (h.scenario_guid IS NOT NULL AND m.scenario_guid=h.scenario_guid)
       OR (h.scenario_guid IS NULL AND ((h.world_guid IS NOT NULL AND m.world_guid=h.world_guid)
           OR (h.world_guid IS NULL AND m.metadata->'data'->'worldAliases' ? h.world_resource)))
) c ON true;
