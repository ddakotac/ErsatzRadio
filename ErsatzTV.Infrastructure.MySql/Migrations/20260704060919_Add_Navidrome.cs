using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErsatzTV.Infrastructure.MySql.Migrations
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
                    Id = table.Column<int>(type: "int", nullable: false),
                    ItemId = table.Column<string>(type: "varchar(36)", unicode: false, maxLength: 36, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ShouldSyncItems = table.Column<bool>(type: "tinyint(1)", nullable: false)
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "NavidromeMediaSource",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    ServerName = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "NavidromeSong",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    ItemId = table.Column<string>(type: "varchar(36)", unicode: false, maxLength: 36, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Etag = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "NavidromeConnection",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Address = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NavidromeMediaSourceId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NavidromeConnection", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NavidromeConnection_NavidromeMediaSource_NavidromeMediaSourc~",
                        column: x => x.NavidromeMediaSourceId,
                        principalTable: "NavidromeMediaSource",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "NavidromePathReplacement",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    NavidromePath = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LocalPath = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NavidromeMediaSourceId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NavidromePathReplacement", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NavidromePathReplacement_NavidromeMediaSource_NavidromeMedia~",
                        column: x => x.NavidromeMediaSourceId,
                        principalTable: "NavidromeMediaSource",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

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
