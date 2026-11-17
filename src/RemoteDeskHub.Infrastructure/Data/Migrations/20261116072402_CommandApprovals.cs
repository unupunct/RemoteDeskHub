using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteDeskHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CommandApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CommandApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComputerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ComputerName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Command = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    Shell = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    RequireElevation = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RequestedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReviewedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ReviewedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReviewNote = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ExecutedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommandApprovals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommandApprovals_ExpiresUtc",
                table: "CommandApprovals",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CommandApprovals_RequestedUtc",
                table: "CommandApprovals",
                column: "RequestedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CommandApprovals_Status",
                table: "CommandApprovals",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommandApprovals");
        }
    }
}
