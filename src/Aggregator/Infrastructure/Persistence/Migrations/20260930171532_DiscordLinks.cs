using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DiscordLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiscordLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DiscordUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    DiscordUserName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LinkedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    PendingCodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PendingCodeExpiresAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscordLinks_Users_Id",
                        column: x => x.Id,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscordLinks_DiscordUserId",
                table: "DiscordLinks",
                column: "DiscordUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscordLinks_PendingCodeHash",
                table: "DiscordLinks",
                column: "PendingCodeHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscordLinks");
        }
    }
}
