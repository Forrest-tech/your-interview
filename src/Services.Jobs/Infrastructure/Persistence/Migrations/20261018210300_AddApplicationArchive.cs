using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Jobs.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 给 applications 表加 IsArchived 列 (Simplify 对等:归档功能)。
    /// </summary>
    public partial class AddApplicationArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE jobs.applications ADD COLUMN IF NOT EXISTS ""IsArchived"" boolean NOT NULL DEFAULT false;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE jobs.applications DROP COLUMN IF EXISTS ""IsArchived"";");
        }
    }
}
