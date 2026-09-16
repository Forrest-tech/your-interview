using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Assessment.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// /practice 页的持久化表(2026-09-15 第十七轮)。
    ///
    /// 背景:此前素材树与录音全在浏览器 localStorage / 内存里,换机器就丢。
    /// 本迁移新增四张表:
    ///   practice_materials        素材树(邻接表 + 同层排序)
    ///   practice_recordings       录音元数据(音频字节在文件系统)
    ///   practice_recording_scores 发音评分结果(1:1,缓存用,避免重复消耗 Azure 额度)
    ///   speech_settings           Azure Speech 密钥/区域(服务端托管,key 永不回传)
    ///
    /// ⚠️ 手写原因:开发容器里没有 dotnet SDK,无法执行 `dotnet ef migrations add`。
    ///    本文件按 EF Core 生成格式逐字段对齐 ModelSnapshot,
    ///    验证方式:`dotnet ef migrations list` 能列出本迁移,
    ///    且 `dotnet ef migrations has-pending-model-changes` 无差异。
    /// </summary>
    public partial class PracticeMaterialsAndRecordings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---------- 素材树 ----------
            migrationBuilder.CreateTable(
                name: "practice_materials",
                schema: "assessment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsExpanded = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_practice_materials", x => x.Id);
                    // 删父节点级联删子树 —— 留孤儿节点比删不干净更糟
                    table.ForeignKey(
                        name: "FK_practice_materials_practice_materials_ParentId",
                        column: x => x.ParentId,
                        principalSchema: "assessment",
                        principalTable: "practice_materials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // 列表查询永远是"我的树",所以按 (UserId, ParentId, SortOrder) 建索引
            migrationBuilder.CreateIndex(
                name: "IX_practice_materials_UserId_ParentId_SortOrder",
                schema: "assessment",
                table: "practice_materials",
                columns: new[] { "UserId", "ParentId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_practice_materials_ParentId",
                schema: "assessment",
                table: "practice_materials",
                column: "ParentId");

            // ---------- 录音元数据 ----------
            // ⚠️ 音频字节**不在这里** —— 落文件系统(Storage:RootDirectory),
            //    本表只存相对路径。二进制大对象塞进 PG 会拖垮备份、让 pg_dump 失控。
            migrationBuilder.CreateTable(
                name: "practice_recordings",
                schema: "assessment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    MaterialId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    SourceLanguage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_practice_recordings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_practice_recordings_UserId_MaterialId_CreatedAt",
                schema: "assessment",
                table: "practice_recordings",
                columns: new[] { "UserId", "MaterialId", "CreatedAt" });

            // ---------- 发音评分(1:1,缓存) ----------
            // 主键即外键 —— 结构上就杜绝"一条录音两份评分"。
            // 拿不到的维度一律 NULL:数据库层也不给默认值,防止有人偷偷塞 0 冒充。
            migrationBuilder.CreateTable(
                name: "practice_recording_scores",
                schema: "assessment",
                columns: table => new
                {
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: false),
                    PronScore = table.Column<double>(type: "double precision", nullable: true),
                    AccuracyScore = table.Column<double>(type: "double precision", nullable: true),
                    FluencyScore = table.Column<double>(type: "double precision", nullable: true),
                    CompletenessScore = table.Column<double>(type: "double precision", nullable: true),
                    ProsodyScore = table.Column<double>(type: "double precision", nullable: true),
                    RecognizedText = table.Column<string>(type: "text", nullable: false),
                    WordsJson = table.Column<string>(type: "jsonb", nullable: true),
                    AzureRegion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ReferenceText = table.Column<string>(type: "text", nullable: false),
                    AssessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_practice_recording_scores", x => x.RecordingId);
                    table.ForeignKey(
                        name: "FK_practice_recording_scores_practice_recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalSchema: "assessment",
                        principalTable: "practice_recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // ---------- Azure Speech 设置 ----------
            // ⚠️ 本表存 key,但**永不**通过任何接口回传;
            //    对外只返回 hasKey / region / 掩码。
            migrationBuilder.CreateTable(
                name: "speech_settings",
                schema: "assessment",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Region = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_speech_settings", x => x.UserId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 顺序必须是先子后父(外键依赖),否则 DropTable 会因约束失败
            migrationBuilder.DropTable(name: "practice_recording_scores", schema: "assessment");
            migrationBuilder.DropTable(name: "speech_settings", schema: "assessment");
            migrationBuilder.DropTable(name: "practice_recordings", schema: "assessment");
            migrationBuilder.DropTable(name: "practice_materials", schema: "assessment");
        }
    }
}
