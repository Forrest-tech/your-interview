using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Jobs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackfillUserIdOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // COD-01/票03 回填(协调者裁决 D):存量行归属现有管理员用户。
            // 管理员 Id 按邮箱子查询现取,禁止硬编码 GUID(各环境 Id 不同)。
            // 守卫:identity 表或管理员不存在时安全跳过(不炸迁移);只填 NULL 行,幂等可重跑。
            // 含软删行一并回填(WHERE 只判 UserId IS NULL,不加 IsDeleted 条件)。
            // 注意:若守卫跳过导致残余 NULL,下一条 contract 迁移的 NOT NULL 会响亮失败——这是预期行为。
            migrationBuilder.Sql(@"
                DO $$
                DECLARE admin_id uuid;
                BEGIN
                    IF EXISTS (SELECT 1 FROM information_schema.tables
                               WHERE table_schema = 'identity' AND table_name = 'users') THEN
                        SELECT ""Id"" INTO admin_id FROM identity.users
                        WHERE ""Email"" = 'admin@your-interview.local' AND NOT ""IsDeleted""
                        LIMIT 1;
                        IF admin_id IS NOT NULL THEN
                            UPDATE jobs.companies SET ""UserId"" = admin_id WHERE ""UserId"" IS NULL;
                            UPDATE jobs.applications SET ""UserId"" = admin_id WHERE ""UserId"" IS NULL;
                            UPDATE jobs.application_communications SET ""UserId"" = admin_id WHERE ""UserId"" IS NULL;
                        END IF;
                    END IF;
                END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 数据回填不可逆:Down 不清空归属(清空会让 contract 回滚后数据失去主人)。
        }
    }
}
