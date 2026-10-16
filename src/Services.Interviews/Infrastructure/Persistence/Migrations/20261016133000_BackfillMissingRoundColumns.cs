using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 回填 20261008010000_AddRoundDetails 可能漏掉的列。
    /// 背景：该 migration 在部分已部署库上被标记为已应用，但 rounds 表实际缺少列
    /// （旧代码从未 Include Rounds，问题直到 Playbook 重做才暴露：Npgsql 42703）。
    /// 用 IF NOT EXISTS 保证幂等，无论列是否存在都可安全执行。
    /// </summary>
    public partial class BackfillMissingRoundColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE interviews.rounds ADD COLUMN IF NOT EXISTS ""MeetingLink"" character varying(2000);");
            migrationBuilder.Sql(@"ALTER TABLE interviews.rounds ADD COLUMN IF NOT EXISTS ""ScheduledTime"" character varying(10);");
            migrationBuilder.Sql(@"ALTER TABLE interviews.rounds ADD COLUMN IF NOT EXISTS ""PrepQuestionsJson"" text;");
            migrationBuilder.Sql(@"ALTER TABLE interviews.rounds ADD COLUMN IF NOT EXISTS ""EmailsJson"" text;");
            migrationBuilder.Sql(@"ALTER TABLE interviews.rounds ADD COLUMN IF NOT EXISTS ""Transcript"" text;");
            migrationBuilder.Sql(@"ALTER TABLE interviews.rounds ADD COLUMN IF NOT EXISTS ""RecordingUrl"" character varying(2000);");
            migrationBuilder.Sql(@"ALTER TABLE interviews.questions ADD COLUMN IF NOT EXISTS ""Mastery"" integer NOT NULL DEFAULT 0;");
            migrationBuilder.Sql(@"ALTER TABLE interviews.entries ADD COLUMN IF NOT EXISTS ""QuestionsToAskJson"" text;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 幂等回填，无 Down 操作（列可能被其他 migration 拥有）。
        }
    }
}
