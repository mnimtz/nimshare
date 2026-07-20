using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NimShare.Api.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// v1.9.1's original V179 was auto-generated after the SqlServer
    /// migrations project was added, and the EF CLI picked the wrong
    /// design-time factory (SqlServer) while comparing against this
    /// project's Sqlite snapshot. Result: a 6000-line "AlterColumn on
    /// every column of every table" migration that on Sqlite triggered
    /// full-table rebuilds and lost the Color/Emoji columns on Folders
    /// (broken Ablage-view on deployed instances).
    ///
    /// This body is deliberately empty. The FK cascade change the
    /// original V179 was meant to capture is a model-only concern on
    /// Sqlite (Sqlite doesn't enforce FKs unless PRAGMA'd, and we don't).
    /// SqlServer picks up the corrected cascade via its own migration
    /// set. Program.cs runs a startup repair for the specific columns
    /// V179 dropped in the field.
    /// </remarks>
    public partial class V179_FolderCascadeToNoAction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) { }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) { }
    }
}
