using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteDeskHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseModelUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProcessId",
                table: "RemoteSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AnyDeskId",
                table: "Computers",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LmViewerId",
                table: "Computers",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MachineId",
                table: "Computers",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RustDeskId",
                table: "Computers",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TeamViewerId",
                table: "Computers",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MachineId",
                table: "Agents",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MachineId",
                table: "AgentEnrollments",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HardwareInventorySnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComputerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", maxLength: 32000, nullable: false),
                    ChangeSummary = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CollectedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HardwareInventorySnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HardwareInventorySnapshots_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Computers_MachineId",
                table: "Computers",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_MachineId",
                table: "Agents",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_HardwareInventorySnapshots_ComputerId_CollectedUtc",
                table: "HardwareInventorySnapshots",
                columns: new[] { "ComputerId", "CollectedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HardwareInventorySnapshots");

            migrationBuilder.DropIndex(
                name: "IX_Computers_MachineId",
                table: "Computers");

            migrationBuilder.DropIndex(
                name: "IX_Agents_MachineId",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "ProcessId",
                table: "RemoteSessions");

            migrationBuilder.DropColumn(
                name: "AnyDeskId",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "LmViewerId",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "MachineId",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "RustDeskId",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "TeamViewerId",
                table: "Computers");

            migrationBuilder.DropColumn(
                name: "MachineId",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "MachineId",
                table: "AgentEnrollments");
        }
    }
}
