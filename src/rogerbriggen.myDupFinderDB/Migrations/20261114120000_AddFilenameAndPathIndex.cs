// Roger Briggen license this file to you under the MIT license.
//

using Microsoft.EntityFrameworkCore.Migrations;

namespace RogerBriggen.myDupFinderDB.Migrations;

/// <summary>
/// The index on ScanItems.FilenameAndPath was declared in the model but never created by a migration.
/// It is used by the resume filter of a scan and by the path lookups.
/// </summary>
public partial class AddFilenameAndPathIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_ScanItems_FilenameAndPath",
            table: "ScanItems",
            column: "FilenameAndPath");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ScanItems_FilenameAndPath",
            table: "ScanItems");
    }
}
