CREATE TABLE gc.account_preferences (
    user_id uuid PRIMARY KEY REFERENCES gc.users(id),
    display_name text NOT NULL DEFAULT '' CHECK (char_length(display_name) <= 40),
    time_zone text NOT NULL DEFAULT 'local' CHECK (time_zone IN ('local','UTC','Europe/Berlin')),
    clock_format text NOT NULL DEFAULT 'auto' CHECK (clock_format IN ('auto','12h','24h')),
    video_volume integer NOT NULL DEFAULT 25 CHECK (video_volume BETWEEN 0 AND 100),
    reduce_motion boolean NOT NULL DEFAULT false
);
