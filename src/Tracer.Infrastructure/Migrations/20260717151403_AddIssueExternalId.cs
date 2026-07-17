using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tracer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIssueExternalId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "Issues",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Issues_TeamId_ExternalId",
                table: "Issues",
                columns: new[] { "TeamId", "ExternalId" },
                unique: true,
                filter: "\"ExternalId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Issues_TeamId_ExternalId",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "Issues");
        }
    }
}
