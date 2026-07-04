using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErsatzTV.Infrastructure.MySql.Migrations
{
    /// <inheritdoc />
    public partial class Add_Audiobookshelf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AudiobookshelfEpisode",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    ItemId = table.Column<string>(type: "varchar(36)", unicode: false, maxLength: 36, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Etag = table.Column<string>(type: "varchar(36)", unicode: false, maxLength: 36, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudiobookshelfEpisode", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AudiobookshelfEpisode_Episode_Id",
                        column: x => x.Id,
                        principalTable: "Episode",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AudiobookshelfLibrary",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    ItemId = table.Column<string>(type: "varchar(36)", unicode: false, maxLength: 36, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ShouldSyncItems = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    AbsMediaType = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudiobookshelfLibrary", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AudiobookshelfLibrary_Library_Id",
                        column: x => x.Id,
                        principalTable: "Library",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AudiobookshelfMediaSource",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    ServerName = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudiobookshelfMediaSource", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AudiobookshelfMediaSource_MediaSource_Id",
                        column: x => x.Id,
                        principalTable: "MediaSource",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AudiobookshelfSeason",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    ItemId = table.Column<string>(type: "varchar(36)", unicode: false, maxLength: 36, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Etag = table.Column<string>(type: "varchar(36)", unicode: false, maxLength: 36, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudiobookshelfSeason", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AudiobookshelfSeason_Season_Id",
                        column: x => x.Id,
                        principalTable: "Season",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AudiobookshelfShow",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    ItemId = table.Column<string>(type: "varchar(36)", unicode: false, maxLength: 36, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Etag = table.Column<string>(type: "varchar(36)", unicode: false, maxLength: 36, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudiobookshelfShow", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AudiobookshelfShow_Show_Id",
                        column: x => x.Id,
                        principalTable: "Show",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AudiobookshelfConnection",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Address = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AudiobookshelfMediaSourceId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudiobookshelfConnection", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AudiobookshelfConnection_AudiobookshelfMediaSource_Audiobook~",
                        column: x => x.AudiobookshelfMediaSourceId,
                        principalTable: "AudiobookshelfMediaSource",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AudiobookshelfPathReplacement",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AudiobookshelfPath = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LocalPath = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AudiobookshelfMediaSourceId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudiobookshelfPathReplacement", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AudiobookshelfPathReplacement_AudiobookshelfMediaSource_Audi~",
                        column: x => x.AudiobookshelfMediaSourceId,
                        principalTable: "AudiobookshelfMediaSource",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookshelfConnection_AudiobookshelfMediaSourceId",
                table: "AudiobookshelfConnection",
                column: "AudiobookshelfMediaSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookshelfEpisode_ItemId",
                table: "AudiobookshelfEpisode",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookshelfLibrary_ItemId",
                table: "AudiobookshelfLibrary",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookshelfPathReplacement_AudiobookshelfMediaSourceId",
                table: "AudiobookshelfPathReplacement",
                column: "AudiobookshelfMediaSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookshelfSeason_ItemId",
                table: "AudiobookshelfSeason",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookshelfShow_ItemId",
                table: "AudiobookshelfShow",
                column: "ItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AudiobookshelfConnection");

            migrationBuilder.DropTable(
                name: "AudiobookshelfEpisode");

            migrationBuilder.DropTable(
                name: "AudiobookshelfLibrary");

            migrationBuilder.DropTable(
                name: "AudiobookshelfPathReplacement");

            migrationBuilder.DropTable(
                name: "AudiobookshelfSeason");

            migrationBuilder.DropTable(
                name: "AudiobookshelfShow");

            migrationBuilder.DropTable(
                name: "AudiobookshelfMediaSource");
        }
    }
}
