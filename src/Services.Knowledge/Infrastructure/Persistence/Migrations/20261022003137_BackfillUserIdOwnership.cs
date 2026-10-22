using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YourInterview.Services.Knowledge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackfillUserIdOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // COD-01 backfill (expand to backfill to contract, middle step):
            // historical entries have no owner info; the sandbox/production data at this
            // point belongs to the single admin account. Look the admin up by email
            // subquery (no hardcoded GUID -- the Id differs per environment).
            // Guard: only run when the identity.users table exists (service databases
            // are deployed independently; if identity was never deployed in this
            // database, skip silently rather than fail the whole migration chain).
            // Only NULL rows are filled (including soft-deleted ones -- the query
            // filter must not resurrect cross-user visibility for deleted rows either),
            // so re-running is idempotent.
            // IMPORTANT: any row still NULL after this backfill will make the following
            // contract migration (RequireUserIdOwnership, SET NOT NULL without a
            // default) fail loudly. That is intentional -- silently defaulting orphan
            // rows to Guid.Empty would hide data problems; fail fast instead.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables
               WHERE table_schema = 'identity' AND table_name = 'users') THEN
        UPDATE knowledge.knowledge_items SET ""UserId"" = (
            SELECT ""Id"" FROM identity.users
            WHERE ""Email"" = 'admin@your-interview.local' AND NOT ""IsDeleted""
            LIMIT 1)
        WHERE ""UserId"" IS NULL;
    END IF;
END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: backfilled ownership cannot be distinguished from real ownership
            // afterwards; rolling back keeps the values (expand step's Down drops
            // the column if a full rollback is really needed).
        }
    }
}
