-- ============================================================================
--  cleanup-recordings.sql — 清除 11 条软删录音行
--
--  Forrest 2026-09-17 要求:"删 11 行"。
--  这 11 条是历史遗留的软删行(IsDeleted=true),其磁盘文件早已不存在。
--  删除前先 SELECT 出全文留档(见随附的导出的 CSV/文本)。
--
--  安全性:
--    · 只删 IsDeleted=true 的行 —— 未删录音一条不动。
--    · 先打印全部内容,再删。
--    · 评分行由外键 ON DELETE CASCADE 自动跟随。
--
--  用法(Mac):
--    "/Library/PostgreSQL/13/bin/psql" -U postgres -d yourinterview \
--      -v ON_ERROR_STOP=1 -f tools/cleanup-recordings.sql
-- ============================================================================

\pset pager off

\echo '===== ① 11 条软删录音(删除前的最后样貌,请留存这段输出) ====='
SELECT "Id","StoragePath","IsDeleted","SizeBytes","DurationSeconds","ContentType","SourceLanguage","CreatedAt"
  FROM assessment.practice_recordings
 WHERE "IsDeleted"
 ORDER BY "CreatedAt";

\echo ''
\echo '===== ② 这 11 条的评分(ReferenceText 是唯一上下文线索) ====='
SELECT s."RecordingId", s."PronScore", s."AccuracyScore",
       left(s."ReferenceText", 300) AS reference_text_head
  FROM assessment.practice_recording_scores s
  JOIN assessment.practice_recordings r ON r."Id" = s."RecordingId"
 WHERE r."IsDeleted"
 ORDER BY s."RecordingId";

\echo ''
\echo '===== ③ 删除前总数 ====='
SELECT count(*) FILTER (WHERE "IsDeleted")     AS soft_deleted,
       count(*) FILTER (WHERE NOT "IsDeleted") AS active,
       count(*)                                AS total
  FROM assessment.practice_recordings;

BEGIN;

DELETE FROM assessment.practice_recording_scores s
 USING assessment.practice_recordings r
 WHERE s."RecordingId" = r."Id" AND r."IsDeleted";

DELETE FROM assessment.practice_recordings WHERE "IsDeleted";

COMMIT;

\echo ''
\echo '===== ④ 删除后总数(soft_deleted 应为 0) ====='
SELECT count(*) FILTER (WHERE "IsDeleted")     AS soft_deleted,
       count(*) FILTER (WHERE NOT "IsDeleted") AS active,
       count(*)                                AS total
  FROM assessment.practice_recordings;
