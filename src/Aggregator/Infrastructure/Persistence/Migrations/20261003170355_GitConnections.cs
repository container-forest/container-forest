using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GitConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Repositories_Name",
                table: "Repositories");

            migrationBuilder.AddColumn<Guid>(
                name: "ConnectionId",
                table: "Repositories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GitConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "NOCASE"),
                    UrlPrefix = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    ProtectedAccessToken = table.Column<string>(type: "TEXT", nullable: true),
                    AccessUserName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    AllowsAllEnvironments = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProtectedWebhookSecret = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GitConnectionEnvironments",
                columns: table => new
                {
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GitConnectionId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitConnectionEnvironments", x => new { x.GitConnectionId, x.EnvironmentId });
                    table.ForeignKey(
                        name: "FK_GitConnectionEnvironments_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GitConnectionEnvironments_GitConnections_GitConnectionId",
                        column: x => x.GitConnectionId,
                        principalTable: "GitConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_ConnectionId",
                table: "Repositories",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_Name",
                table: "Repositories",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_GitConnectionEnvironments_EnvironmentId",
                table: "GitConnectionEnvironments",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_GitConnections_Name",
                table: "GitConnections",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Repositories_GitConnections_ConnectionId",
                table: "Repositories",
                column: "ConnectionId",
                principalTable: "GitConnections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Repositories_GitConnections_ConnectionId",
                table: "Repositories");

            migrationBuilder.DropTable(
                name: "GitConnectionEnvironments");

            migrationBuilder.DropTable(
                name: "GitConnections");

            migrationBuilder.DropIndex(
                name: "IX_Repositories_ConnectionId",
                table: "Repositories");

            migrationBuilder.DropIndex(
                name: "IX_Repositories_Name",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "ConnectionId",
                table: "Repositories");

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_Name",
                table: "Repositories",
                column: "Name",
                unique: true);
        }
    }
}
