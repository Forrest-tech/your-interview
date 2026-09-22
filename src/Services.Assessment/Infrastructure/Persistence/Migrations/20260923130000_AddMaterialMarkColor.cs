using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Assessment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMaterialMarkColor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ★ 第四十三轮(Forrest):素材标记色 —— 标记属于素材条目本身,
            //   存离散色键(orange/red/green),null = 无标记。
            //   之后前端可按颜色筛选(如"列出所有红色标记的素材")。
            migrationBuilder.AddColumn<string>(
                name: "MarkColor",
                schema: "assessment",
                table: "practice_materials",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MarkColor",
                schema: "assessment",
                table: "practice_materials");
        }
    }
}
