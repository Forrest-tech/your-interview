-- ============================================================================
--  purge-all-recordings.sql — 删除【全部】录音(11 条软删 + 6 条正常)
--
--  Forrest 2026-09-17 明确要求:"11 条直接删除,6 条正常也删除。"
--  即:清空 practice_recordings 全表(及其评分)。
--
--  注意:6 条正常录音的磁盘音频文件**仍存在**,删行后会成为孤儿文件。
--  脚本末尾会打印它们的 StoragePath,便于一并删除磁盘文件。
--
--  用法:
--    PGPASSWORD=*** "/Library/PostgreSQL/13/bin/psql" -U postgres -d yourinterview \
--      -v ON_ERROR_STOP=1 -f tools/purge-all-recordings.sql
-- ============================================================================

\pset pager off

\echo '===== ① 删除前:全部录音(含磁盘路径,请留存) ====='
SELECT "Id","StoragePath","IsDeleted","SizeBytes","DurationSeconds","CreatedAt"
  FROM assessment.practice_recordings
 ORDER BY "IsDeleted","CreatedAt";

\echo ''
\echo '===== ② 删除前总数 ====='
SELECT count(*) FILTER (WHERE "IsDeleted")     AS soft_deleted,
       count(*) FILTER (WHERE NOT "IsDeleted") AS active,
       count(*)                                AS total
  FROM assessment.practice_recordings;

BEGIN;

DELETE FROM assessment.practice_recording_scores;
DELETE FROM assessment.practice_recordings;

COMMIT;

\echo ''
\echo '===== ③ 删除后总数(应全部为 0) ====='
SELECT count(*) AS remaining_recordings FROM assessment.practice_recordings;
SELECT count(*) AS remaining_scores     FROM assessment.practice_recording_scores;

\echo ''
\echo '===== ④ 磁盘侧:以下文件现已无库行认领,可一并删除 ====='
\echo '(在 Mac 上执行: find storage/recordings -type f)'
