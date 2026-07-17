using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tracer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIssueAssigneeAndSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Issues_StateId",
                table: "Issues");

            migrationBuilder.AddColumn<string>(
                name: "Assignee",
                table: "Issues",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Issues_StateId_Position",
                table: "Issues",
                columns: new[] { "StateId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_Issues_TeamId_Assignee",
                table: "Issues",
                columns: new[] { "TeamId", "Assignee" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Issues_StateId_Position",
                table: "Issues");

            migrationBuilder.DropIndex(
                name: "IX_Issues_TeamId_Assignee",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "Assignee",
                table: "Issues");

            migrationBuilder.CreateIndex(
                name: "IX_Issues_StateId",
                table: "Issues",
                column: "StateId");
        }
    }
}
