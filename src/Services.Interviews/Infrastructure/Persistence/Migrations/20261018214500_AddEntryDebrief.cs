using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 给 entries 表加 DebriefJson 列 (复盘从 localStorage 迁后端)。
    /// </summary>
    public partial class AddEntryDebrief : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE interviews.entries ADD COLUMN IF NOT EXISTS ""DebriefJson"" text;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE interviews.entries DROP COLUMN IF EXISTS ""DebriefJson"";");
        }
    }
}
