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
            migrationBuilder.Sql("""
                INSERT INTO aigateway.ai_settings
                    ("UserId","Protocol","ApiKey","BaseUrl","Model","Endpoint","ApiVersion","DisplayName","UpdatedAt","CreatedAt")
                SELECT s."UserId", s."Protocol", s."ApiKey", s."BaseUrl", s."Model", s."Endpoint",
                       s."ApiVersion", s."DisplayName", s."UpdatedAt", s."UpdatedAt"
                FROM assessment.ai_settings s
                WHERE NOT EXISTS (
                    SELECT 1 FROM aigateway.ai_settings t WHERE t."UserId" = s."UserId");
                """);

            // 2) 旧表退场 —— 凭据的唯一家从今往后是 aigateway.ai_settings
            migrationBuilder.Sql("DROP TABLE assessment.ai_settings;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE assessment.ai_settings (
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

                INSERT INTO assessment.ai_settings
                    ("UserId","Protocol","ApiKey","BaseUrl","Model","Endpoint","ApiVersion","DisplayName","UpdatedAt")
                SELECT t."UserId", t."Protocol", t."ApiKey", LEFT(t."BaseUrl", 500), t."Model", LEFT(t."Endpoint", 500),
                       t."ApiVersion", LEFT(t."DisplayName", 100), t."UpdatedAt"
                FROM aigateway.ai_settings t;
                """);
        }
    }
}
