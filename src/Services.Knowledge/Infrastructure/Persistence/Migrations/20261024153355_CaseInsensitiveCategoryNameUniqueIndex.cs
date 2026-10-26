using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Knowledge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CaseInsensitiveCategoryNameUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tech_stack_categories_UserId_Name",
                schema: "knowledge",
                table: "tech_stack_categories");

            // 票96:EF fluent API 无法声明表达式索引,大小写不敏感唯一约束以显式
            // SQL 落库。模型与快照不再声明分类名唯一索引(见 DbContext 注释);
            // 该约束只存在于本迁移的 SQL 中,后续脚手架因模型无此声明而无 diff,
            // 不会动它。注意:若存量库已存在大小写变体重复行,本句执行失败 ——
            // 属预期,须先按票96报告的处置方案处理存量行,绝不在迁移里静默
            // 合并/删数据。
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_tech_stack_categories_UserId_LowerName"
                ON "knowledge"."tech_stack_categories" ("UserId", lower("Name"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "knowledge"."IX_tech_stack_categories_UserId_LowerName";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_tech_stack_categories_UserId_Name",
                schema: "knowledge",
                table: "tech_stack_categories",
                columns: new[] { "UserId", "Name" },
                unique: true);
        }
    }
}
