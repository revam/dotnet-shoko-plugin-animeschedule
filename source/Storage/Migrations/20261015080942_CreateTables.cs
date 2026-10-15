using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shoko.Plugin.AnimeSchedule.Storage.Migrations
{
    /// <inheritdoc />
    public partial class CreateTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Catalog",
                columns: table => new
                {
                    Route = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AnidbID = table.Column<int>(type: "INTEGER", nullable: true),
                    AnilistID = table.Column<int>(type: "INTEGER", nullable: true),
                    MalID = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Episodes = table.Column<int>(type: "INTEGER", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Catalog", x => x.Route);
                });

            migrationBuilder.CreateTable(
                name: "Lookups",
                columns: table => new
                {
                    AnidbAnimeID = table.Column<int>(type: "INTEGER", nullable: false),
                    Route = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    LastCheckedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextCheckAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConsecutiveMisses = table.Column<int>(type: "INTEGER", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lookups", x => x.AnidbAnimeID);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Catalog_AnidbID",
                table: "Catalog",
                column: "AnidbID");

            migrationBuilder.CreateIndex(
                name: "IX_Catalog_AnilistID",
                table: "Catalog",
                column: "AnilistID");

            migrationBuilder.CreateIndex(
                name: "IX_Catalog_MalID",
                table: "Catalog",
                column: "MalID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Catalog");

            migrationBuilder.DropTable(
                name: "Lookups");
        }
    }
}
