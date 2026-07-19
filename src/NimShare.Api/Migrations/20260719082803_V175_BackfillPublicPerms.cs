using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NimShare.Api.Migrations
{
    /// <summary>
    /// V174 added PublicCanRead/Write/Delete with defaultValue=false, but the
    /// entity constructor defaults Read+Write to true. Users that existed
    /// before v1.7.5 lost their Public library access on that migration —
    /// the browse tree hid every Public file for them and uploads 403'd.
    /// This backfill grants Read+Write to everyone, matching new-user defaults.
    /// Delete stays off (admin-configurable via the User edit page).
    /// </summary>
    public partial class V175_BackfillPublicPerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"UPDATE Users SET PublicCanRead = 1, PublicCanWrite = 1 WHERE PublicCanRead = 0 AND PublicCanWrite = 0 AND PublicCanDelete = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: we can't reliably distinguish backfilled rows from
            // legitimately-set ones. Rolling back would risk zeroing
            // permissions that admins explicitly granted after the backfill.
        }
    }
}
