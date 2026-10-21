using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Assessment.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// M2.3(2026-09-24):LLM 凭据收敛到 AiGateway。
    ///
    /// 脑裂修复:此前前端把 LLM key 存进 assessment.ai_settings,而 Jobs 的
    /// Cover Letter 生成读的是 aigateway.ai_settings —— 用户保存的 key 永远不生效。
    /// 收敛后凭据只存 aigateway 一处,本迁移把历史行搬过去后删除旧表:
    ///   1. aigateway.ai_settings 里没有该用户时才插入(同一用户以网关侧为准,幂等);
    ///   2. 删除 assessment.ai_settings,模型快照同步移除该实体。
    /// Down():重建旧表并尽量搬回(超宽截断到旧列宽),用于紧急回滚。
    /// 手写迁移(沙箱无 dotnet-ef);[DbContext]/[Migration] 属性与目标模型在 Designer 文件里。
    /// </summary>
    public partial class RemoveAiSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) 历史凭据搬家:同一用户以网关侧已存的为准(幂等)
            // ★ REQ-01/票02 存在性守卫:本迁移是跨 schema 搬运,依赖 aigateway.ai_settings
            //   已由 AiGateway 服务建好。全新环境下若 Assessment 先于 AiGateway 冷启动,
            //   目标表不存在 —— 旧裸 SQL 直接 42P01,整个 MigrateAsync 中断,其后全部迁移
            //   (practice_categories 等)永不应用,模块恒 500。故先查 information_schema:
            //   源表或目标表任一不存在时安全跳过搬运(没有目标表时无处可搬,凭据本就只应
            //   由 AiGateway 持有;源表不存在说明无历史数据可搬),两表都在才执行原 INSERT。
            //   守卫幂等:重复执行不重复插入(NOT EXISTS)、不报错。
            //   取舍:本迁移已在部分库应用过 —— EF Core 只按 MigrationId 记录应用历史,
            //   不校验已应用迁移的代码内容哈希,改 Up 的 SQL 不会使既有库报错或重跑;
            //   尚未应用到本迁移的库则走带守卫的新逻辑,正是要修的人群。
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM information_schema.tables
                               WHERE table_schema = 'aigateway' AND table_name = 'ai_settings')
                       AND EXISTS (SELECT 1 FROM information_schema.tables
                                   WHERE table_schema = 'assessment' AND table_name = 'ai_settings') THEN
                        INSERT INTO aigateway.ai_settings
                            ("UserId","Protocol","ApiKey","BaseUrl","Model","Endpoint","ApiVersion","DisplayName","UpdatedAt","CreatedAt")
                        SELECT s."UserId", s."Protocol", s."ApiKey", s."BaseUrl", s."Model", s."Endpoint",
                               s."ApiVersion", s."DisplayName", s."UpdatedAt", s."UpdatedAt"
                        FROM assessment.ai_settings s
                        WHERE NOT EXISTS (
                            SELECT 1 FROM aigateway.ai_settings t WHERE t."UserId" = s."UserId");
                    END IF;
                END $$;
                """);

            // 2) 旧表退场 —— 凭据的唯一家从今往后是 aigateway.ai_settings
            //    IF EXISTS:源表缺失时同样安全跳过,与上面守卫口径一致。
            migrationBuilder.Sql("DROP TABLE IF EXISTS assessment.ai_settings;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS assessment.ai_settings (
                    "UserId" uuid NOT NULL,
                    "Protocol" character varying(50) NOT NULL,
                    "ApiKey" text NOT NULL,
                    "BaseUrl" character varying(500),
                    "Model" character varying(200) NOT NULL,
                    "Endpoint" character varying(500),
                    "ApiVersion" character varying(50),
                    "DisplayName" character varying(100),
                    "UpdatedAt" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_ai_settings" PRIMARY KEY ("UserId")
                );
                """);

            // 回搬同样加存在性守卫(REQ-01/票02):aigateway.ai_settings 不存在时跳过,
            // 且 NOT EXISTS 保证重复回滚不撞主键 —— 与 Up 的守卫口径一致。
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM information_schema.tables
                               WHERE table_schema = 'aigateway' AND table_name = 'ai_settings') THEN
                        INSERT INTO assessment.ai_settings
                            ("UserId","Protocol","ApiKey","BaseUrl","Model","Endpoint","ApiVersion","DisplayName","UpdatedAt")
                        SELECT t."UserId", t."Protocol", t."ApiKey", LEFT(t."BaseUrl", 500), t."Model", LEFT(t."Endpoint", 500),
                               t."ApiVersion", LEFT(t."DisplayName", 100), t."UpdatedAt"
                        FROM aigateway.ai_settings t
                        WHERE NOT EXISTS (
                            SELECT 1 FROM assessment.ai_settings s WHERE s."UserId" = t."UserId");
                    END IF;
                END $$;
                """);
        }
    }
}
