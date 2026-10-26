using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Assessment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlignModelSnapshot : Migration
    {
        // ★ 票87 D1 手工对齐迁移:本迁移**有意无任何数据库操作**(Up/Down 均为空)。
        //   目的仅是更新 Designer + ModelSnapshot,使快照与当前模型一致——快照剔除
        //   已删实体 UserResume(票17 删代码)。票17 决策「留表留数据」:assessment.resumes
        //   表及存量数据保留为冻结孤儿表,绝不删表。scaffold 原稿的 Up 只有一条针对
        //   resumes 的删表操作(D2 已由 DbContext 补 HasMaxLength(20) 消除,无改列操作),
        //   已手工剔除;原稿全文存 evidence-87/scaffolded-migration-raw.cs。
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
