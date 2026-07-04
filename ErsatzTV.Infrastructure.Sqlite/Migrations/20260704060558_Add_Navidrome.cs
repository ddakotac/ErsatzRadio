using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErsatzTV.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class Add_Navidrome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NavidromeLibrary",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemId = table.Column<string>(type: "TEXT", unicode: false, maxLength: 36, nullable: true),
                    ShouldSyncItems = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NavidromeLibrary", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NavidromeLibrary_Library_Id",
                        column: x => x.Id,
                        principalTable: "Library",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NavidromeMediaSource",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ServerName = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NavidromeMediaSource", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NavidromeMediaSource_MediaSource_Id",
                        column: x => x.Id,
                        principalTable: "MediaSource",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NavidromeSong",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemId = table.Column<string>(type: "TEXT", unicode: false, maxLength: 36, nullable: true),
                    Etag = table.Column<string>(type: "TEXT", unicode: false, maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NavidromeSong", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NavidromeSong_Song_Id",
                        column: x => x.Id,
                        principalTable: "Song",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NavidromeConnection",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Address = table.Column<string>(type: "TEXT", nullable: true),
                    NavidromeMediaSourceId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NavidromeConnection", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NavidromeConnection_NavidromeMediaSource_NavidromeMediaSourceId",
                        column: x => x.NavidromeMediaSourceId,
                        principalTable: "NavidromeMediaSource",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NavidromePathReplacement",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NavidromePath = table.Column<string>(type: "TEXT", nullable: true),
                    LocalPath = table.Column<string>(type: "TEXT", nullable: true),
                    NavidromeMediaSourceId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NavidromePathReplacement", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NavidromePathReplacement_NavidromeMediaSource_NavidromeMediaSourceId",
                        column: x => x.NavidromeMediaSourceId,
                        principalTable: "NavidromeMediaSource",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NavidromeConnection_NavidromeMediaSourceId",
                table: "NavidromeConnection",
                column: "NavidromeMediaSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_NavidromeLibrary_ItemId",
                table: "NavidromeLibrary",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_NavidromePathReplacement_NavidromeMediaSourceId",
                table: "NavidromePathReplacement",
                column: "NavidromeMediaSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_NavidromeSong_ItemId",
                table: "NavidromeSong",
                column: "ItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NavidromeConnection");

            migrationBuilder.DropTable(
                name: "NavidromeLibrary");

            migrationBuilder.DropTable(
                name: "NavidromePathReplacement");

            migrationBuilder.DropTable(
                name: "NavidromeSong");

            migrationBuilder.DropTable(
                name: "NavidromeMediaSource");
        }
    }
}
