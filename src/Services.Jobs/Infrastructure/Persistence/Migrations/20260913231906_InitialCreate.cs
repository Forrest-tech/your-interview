using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Jobs.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "jobs");

            migrationBuilder.CreateTable(
                name: "companies",
                schema: "jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Website = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Industry = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Location = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    LogoUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    IsBlacklisted = table.Column<bool>(type: "boolean", nullable: false),
                    EmployeeCount = table.Column<int>(type: "integer", nullable: true),
                    CompanyType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_companies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "applications",
                schema: "jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Location = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Link = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Salary = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WorkMode = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    JdSummary = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AppliedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Deadline = table.Column<DateOnly>(type: "date", nullable: true),
                    NextFollowUpAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResumeScore = table.Column<int>(type: "integer", nullable: true),
                    PassRateEstimate = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    MatchKeywords = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Notes = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    PosterName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    NeedsConnectFirst = table.Column<bool>(type: "boolean", nullable: false),
                    OutreachMessage = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    OutreachSentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_applications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_applications_companies_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "jobs",
                        principalTable: "companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "application_status_changes",
                schema: "jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_status_changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_application_status_changes_applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalSchema: "jobs",
                        principalTable: "applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "interview_rounds",
                schema: "jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Stage = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ScheduledDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Interviewer = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Format = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Feedback = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_interview_rounds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_interview_rounds_applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalSchema: "jobs",
                        principalTable: "applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_application_status_changes_ApplicationId",
                schema: "jobs",
                table: "application_status_changes",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_applications_CompanyId",
                schema: "jobs",
                table: "applications",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_applications_Status",
                schema: "jobs",
                table: "applications",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_applications_Status_Priority",
                schema: "jobs",
                table: "applications",
                columns: new[] { "Status", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_companies_Name",
                schema: "jobs",
                table: "companies",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_interview_rounds_ApplicationId",
                schema: "jobs",
                table: "interview_rounds",
                column: "ApplicationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "application_status_changes",
                schema: "jobs");

            migrationBuilder.DropTable(
                name: "interview_rounds",
                schema: "jobs");

            migrationBuilder.DropTable(
                name: "applications",
                schema: "jobs");

            migrationBuilder.DropTable(
                name: "companies",
                schema: "jobs");
        }
    }
}
