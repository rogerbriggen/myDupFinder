using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RogerBriggen.myDupFinderDB.Migrations
{
    public partial class InitalCreate : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScanErrorItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FilenameAndPath = table.Column<string>(type: "TEXT", nullable: false),
                    Filename = table.Column<string>(type: "TEXT", nullable: false),
                    PathBase = table.Column<string>(type: "TEXT", nullable: false),
                    ScanExecutionComputer = table.Column<string>(type: "TEXT", nullable: false),
                    OriginComputer = table.Column<string>(type: "TEXT", nullable: false),
                    ScanName = table.Column<string>(type: "TEXT", nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    FileCreationUTC = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FileLastModificationUTC = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ErrorOccurrence = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MyException = table.Column<string>(type: "TEXT", nullable: false),
                    DateRunStartedUTC = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanErrorItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScanItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FilenameAndPath = table.Column<string>(type: "TEXT", nullable: false),
                    Filename = table.Column<string>(type: "TEXT", nullable: false),
                    PathBase = table.Column<string>(type: "TEXT", nullable: false),
                    ScanExecutionComputer = table.Column<string>(type: "TEXT", nullable: false),
                    OriginComputer = table.Column<string>(type: "TEXT", nullable: false),
                    ScanName = table.Column<string>(type: "TEXT", nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    FileCreationUTC = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FileLastModificationUTC = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FileSha512Hash = table.Column<string>(type: "TEXT", nullable: false),
                    FirstScanDateUTC = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastScanDateUTC = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSha512ScanDateUTC = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScanItems_Filename",
                table: "ScanItems",
                column: "Filename");

            migrationBuilder.CreateIndex(
                name: "IX_ScanItems_FileSha512Hash",
                table: "ScanItems",
                column: "FileSha512Hash");

            migrationBuilder.CreateIndex(
                name: "IX_ScanItems_FileSize",
                table: "ScanItems",
                column: "FileSize");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScanErrorItems");

            migrationBuilder.DropTable(
                name: "ScanItems");
        }
    }
}
