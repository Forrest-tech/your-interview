using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 给 rounds 表加 ChecklistJson 列 (准备清单)。
    /// </summary>
    public partial class AddRoundChecklist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE interviews.rounds ADD COLUMN IF NOT EXISTS ""ChecklistJson"" text;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE interviews.rounds DROP COLUMN IF EXISTS ""ChecklistJson"";");
        }
    }
}
