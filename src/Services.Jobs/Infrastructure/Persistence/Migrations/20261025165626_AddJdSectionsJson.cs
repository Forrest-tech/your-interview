using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Jobs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJdSectionsJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 票 154：只加 SectionsJson 一列。注意：ef 生成时曾连带一个 interview_rounds.xmin 列
            // （模型里 rounds 配了 xmin 并发令牌、但历史快照从未收录，属既有漂移），
            // 本票刻意不带它 —— 既保持迁移纯净，也避免在已有 xmin 列的库上重建列失败。
            // 该漂移另行立项处理，本票不碰。
            migrationBuilder.AddColumn<string>(
                name: "SectionsJson",
                schema: "jobs",
                table: "applications",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SectionsJson",
                schema: "jobs",
                table: "applications");
        }
    }
}
