using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Assessment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPracticeCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ★ 2026-09-25(Forrest):练习类别 —— 素材树按场景过滤
            //   (工作面试 / 生活英语 / 自定义...),下拉一选只看这一类。
            migrationBuilder.CreateTable(
                name: "practice_categories",
                schema: "assessment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_practice_categories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_practice_categories_UserId_Name",
                schema: "assessment",
                table: "practice_categories",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_practice_categories_UserId_SortOrder",
                schema: "assessment",
                table: "practice_categories",
                columns: new[] { "UserId", "SortOrder" });

            // 根级素材挂类别;删类别时 SetNull —— 素材自动回"未分类",数据零丢失。
            migrationBuilder.AddColumn<Guid>(
                name: "CategoryId",
                schema: "assessment",
                table: "practice_materials",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_practice_materials_CategoryId",
                schema: "assessment",
                table: "practice_materials",
                column: "CategoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_practice_materials_practice_categories_CategoryId",
                schema: "assessment",
                table: "practice_materials",
                column: "CategoryId",
                principalSchema: "assessment",
                principalTable: "practice_categories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_practice_materials_practice_categories_CategoryId",
                schema: "assessment",
                table: "practice_materials");

            migrationBuilder.DropIndex(
                name: "IX_practice_materials_CategoryId",
                schema: "assessment",
                table: "practice_materials");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                schema: "assessment",
                table: "practice_materials");

            migrationBuilder.DropTable(
                name: "practice_categories",
                schema: "assessment");
        }
    }
}
