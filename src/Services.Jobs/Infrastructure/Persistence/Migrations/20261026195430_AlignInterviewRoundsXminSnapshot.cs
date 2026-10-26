using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Jobs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlignInterviewRoundsXminSnapshot : Migration
    {
        // ★ 票222 手工对齐迁移:本迁移**有意无任何数据库操作**(Up/Down 均为空)。
        //   目的仅是更新 Designer + ModelSnapshot,使快照与当前模型一致——快照补录
        //   JobsDbContext.InterviewRound 的 xmin 并发令牌映射(票101,interview_rounds 表)。
        //   xmin 是 PostgreSQL 系统列,每行天然存在;票101 的“零迁移”指运行时无需 DDL
        //   (令牌读系统列即可);但 EF differ 仍把 UseXminAsConcurrencyToken 序列化进快照,
        //   故 has-pending 恒报漂移,票154 迁移 scaffold 也曾捎带此列(已剥离)。
        //   scaffold 原稿的 Up 只有 1 条 AddColumn xmin,已手工剔除:真实 PG 拒绝用户列
        //   与系统列同名,该 AddColumn 永远无法 apply,放任则下个 Jobs 迁移票 scaffold 必
        //   捎带 1 条不可 apply 的操作。本迁移仅写 history 行,零 DDL,运行时并发行为不变
        //   (令牌映射早已生效)。Designer + Snapshot 为 scaffold 原样,未手改。
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
