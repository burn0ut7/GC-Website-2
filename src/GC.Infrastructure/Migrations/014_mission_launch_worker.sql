ALTER TABLE gc.mission_launch_requests
 ADD COLUMN claim_id uuid,
 ADD COLUMN claimed_at timestamptz,
 ADD COLUMN finished_at timestamptz,
 ADD COLUMN outcome jsonb;
CREATE UNIQUE INDEX mission_launch_claim ON gc.mission_launch_requests(server_id,claim_id) WHERE claim_id IS NOT NULL;
CREATE UNIQUE INDEX mission_launch_one_running ON gc.mission_launch_requests(server_id) WHERE status='running';
