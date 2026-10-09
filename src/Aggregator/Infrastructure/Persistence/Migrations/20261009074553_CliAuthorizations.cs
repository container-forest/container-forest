using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CliAuthorizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CliAuthorizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceCodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserCodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ClientName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    HostName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RequestedFrom = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastPolledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    TokenName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TokenReadOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    TokenRestricted = table.Column<bool>(type: "INTEGER", nullable: false),
                    TokenExpiresAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CliAuthorizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CliAuthorizations_Users_ApprovedBy",
                        column: x => x.ApprovedBy,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CliAuthorizationEnvironments",
                columns: table => new
                {
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CliAuthorizationId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CliAuthorizationEnvironments", x => new { x.CliAuthorizationId, x.EnvironmentId });
                    table.ForeignKey(
                        name: "FK_CliAuthorizationEnvironments_CliAuthorizations_CliAuthorizationId",
                        column: x => x.CliAuthorizationId,
                        principalTable: "CliAuthorizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CliAuthorizationEnvironments_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CliAuthorizationEnvironments_EnvironmentId",
                table: "CliAuthorizationEnvironments",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_CliAuthorizations_ApprovedBy",
                table: "CliAuthorizations",
                column: "ApprovedBy");

            migrationBuilder.CreateIndex(
                name: "IX_CliAuthorizations_DeviceCodeHash",
                table: "CliAuthorizations",
                column: "DeviceCodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CliAuthorizations_ExpiresAt",
                table: "CliAuthorizations",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_CliAuthorizations_UserCodeHash",
                table: "CliAuthorizations",
                column: "UserCodeHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CliAuthorizationEnvironments");

            migrationBuilder.DropTable(
                name: "CliAuthorizations");
        }
    }
}
