-- Manual fix for missing AddRoundDetails migration columns
-- Run this in PostgreSQL if `docker compose up -d --build` was done before `git pull`
-- Database: your-interview, Schema: interviews, Table: rounds

ALTER TABLE interviews.rounds ADD COLUMN IF NOT EXISTS "MeetingLink" character varying(2000);
ALTER TABLE interviews.rounds ADD COLUMN IF NOT EXISTS "ScheduledTime" character varying(10);
ALTER TABLE interviews.rounds ADD COLUMN IF NOT EXISTS "PrepQuestionsJson" text;
ALTER TABLE interviews.rounds ADD COLUMN IF NOT EXISTS "EmailsJson" text;
ALTER TABLE interviews.rounds ADD COLUMN IF NOT EXISTS "Transcript" text;
ALTER TABLE interviews.rounds ADD COLUMN IF NOT EXISTS "RecordingUrl" character varying(2000);

-- Mark migration as applied (so EF doesn't try to re-run it)
INSERT INTO interviews."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261008010000_AddRoundDetails', '8.0.0')
ON CONFLICT ("MigrationId") DO NOTHING;
