using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteDeskHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SharedFolderSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SharedFolderAgentFiles",
                columns: table => new
                {
                    ComputerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CentralSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    LastWriteUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ObservedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedFolderAgentFiles", x => new { x.ComputerId, x.Path });
                    table.ForeignKey(
                        name: "FK_SharedFolderAgentFiles_Computers_ComputerId",
                        column: x => x.ComputerId,
                        principalTable: "Computers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SharedFolderFiles",
                columns: table => new
                {
                    Path = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    LastWriteUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    SourceComputerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceComputerName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedFolderFiles", x => x.Path);
                });

            migrationBuilder.CreateTable(
                name: "SharedFolderLocalFiles",
                columns: table => new
                {
                    Path = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CentralSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    LastWriteUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ObservedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedFolderLocalFiles", x => x.Path);
                });

            migrationBuilder.CreateTable(
                name: "SharedFolderSyncState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LastScanUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastCompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    LastErrorUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Files = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Agents = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedFolderSyncState", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SharedFolderAgentFiles_ObservedUtc",
                table: "SharedFolderAgentFiles",
                column: "ObservedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SharedFolderFiles_IsDeleted",
                table: "SharedFolderFiles",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_SharedFolderFiles_UpdatedUtc",
                table: "SharedFolderFiles",
                column: "UpdatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SharedFolderLocalFiles_ObservedUtc",
                table: "SharedFolderLocalFiles",
                column: "ObservedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SharedFolderAgentFiles");

            migrationBuilder.DropTable(
                name: "SharedFolderFiles");

            migrationBuilder.DropTable(
                name: "SharedFolderLocalFiles");

            migrationBuilder.DropTable(
                name: "SharedFolderSyncState");
        }
    }
}
