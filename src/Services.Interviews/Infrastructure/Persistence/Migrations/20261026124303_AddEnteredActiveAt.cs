using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Interviews.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEnteredActiveAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 票186 三步同一迁移,不可拆成两次部署:
            // 1. 可空建列(不用 0001-01-01 默认值占位,避免脏数据混入业务语义);
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EnteredActiveAt",
                schema: "interviews",
                table: "entries",
                type: "timestamp with time zone",
                nullable: true);
            // 2. 回填存量行 = CreatedAt。
            // 诚实边界:回填值 = “首次进入 Active 的时刻”在未发生过重返的前提下成立;
            // 历史重返次数无法重建(无状态历史表),存量行若曾重返过,其回填值会早于
            // 真实的最近一次进入 —— 这是接受为未知,不是“近似准确”,绝不许用任何
            // 启发式(首轮日期、Result 文本时间等)去“猜”一个更真的值。
            // 迁移生效后的一切重返均由 RefreshEnteredActiveAt 精确记录。
            // 列名口径:本库 EF 约定沿用属性 PascalCase 列名(CreatedAt/UpdatedAt 同例),
            // 故 SQL 必须用双引号 PascalCase 标识符;无引号小写会因 PG 折叠而找不到列。
            migrationBuilder.Sql(
                @"UPDATE interviews.entries SET ""EnteredActiveAt"" = ""CreatedAt"";");
            // 3. 收紧为非空(回填后无 NULL 行,此步必成功;失败即 fail-fast,绝不手工补账)。
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "EnteredActiveAt",
                schema: "interviews",
                table: "entries",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnteredActiveAt",
                schema: "interviews",
                table: "entries");
        }
    }
}
