using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Assessment.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TtsAudioCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "practice_tts_cache",
                schema: "assessment",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CacheKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    TextHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Voice = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Speed = table.Column<double>(type: "double precision", nullable: false),
                    Language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    HitCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_practice_tts_cache", x => new { x.UserId, x.CacheKey });
                });

            migrationBuilder.CreateIndex(
                name: "IX_practice_tts_cache_UserId_TextHash",
                schema: "assessment",
                table: "practice_tts_cache",
                columns: new[] { "UserId", "TextHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "practice_tts_cache",
                schema: "assessment");
        }
    }
}
