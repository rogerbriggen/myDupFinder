// Roger Briggen license this file to you under the MIT license.
//

using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RogerBriggen.myDupFinderDB.Migrations;

/// <summary>
/// Adds the ScanVolumes table: the identity of the volume of each scanned base path, so refresh, scan and check can notice an unmounted or exchanged disk.
/// Existing rows are not touched; the table starts empty and is filled by the next scan or refresh.
/// </summary>
public partial class AddScanVolumes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ScanVolumes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                OriginComputer = table.Column<string>(type: "TEXT", nullable: false),
                ScanName = table.Column<string>(type: "TEXT", nullable: false),
                BasePath = table.Column<string>(type: "TEXT", nullable: false),
                VolumeIdentity = table.Column<string>(type: "TEXT", nullable: false),
                FirstRecordedUTC = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastVerifiedUTC = table.Column<DateTime>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ScanVolumes", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ScanVolumes_OriginComputer_ScanName_BasePath",
            table: "ScanVolumes",
            columns: new[] { "OriginComputer", "ScanName", "BasePath" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ScanVolumes");
    }
}
