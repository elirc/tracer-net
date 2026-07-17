using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tracer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIssueRelationsAndSubIssues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                table: "Issues",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IssueRelations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceIssueId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetIssueId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueRelations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IssueRelations_Issues_SourceIssueId",
                        column: x => x.SourceIssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IssueRelations_Issues_TargetIssueId",
                        column: x => x.TargetIssueId,
                        principalTable: "Issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Issues_ParentId",
                table: "Issues",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueRelations_SourceIssueId_TargetIssueId_Type",
                table: "IssueRelations",
                columns: new[] { "SourceIssueId", "TargetIssueId", "Type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IssueRelations_SourceIssueId_Type",
                table: "IssueRelations",
                columns: new[] { "SourceIssueId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_IssueRelations_TargetIssueId_Type",
                table: "IssueRelations",
                columns: new[] { "TargetIssueId", "Type" });

            migrationBuilder.AddForeignKey(
                name: "FK_Issues_Issues_ParentId",
                table: "Issues",
                column: "ParentId",
                principalTable: "Issues",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Issues_Issues_ParentId",
                table: "Issues");

            migrationBuilder.DropTable(
                name: "IssueRelations");

            migrationBuilder.DropIndex(
                name: "IX_Issues_ParentId",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "Issues");
        }
    }
}
