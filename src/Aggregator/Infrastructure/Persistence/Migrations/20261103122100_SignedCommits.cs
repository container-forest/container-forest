using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SignedCommits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequireImageDigests",
                table: "Stacks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireSignedCommits",
                table: "Stacks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedCommit",
                table: "Deployments",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Endorsement",
                table: "Deployments",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                // Earlier deployments were never checked (CommitEndorsement.NotChecked, stored by name).
                defaultValue: "NotChecked");

            migrationBuilder.AddColumn<string>(
                name: "EndorsementProblem",
                table: "Deployments",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SignedBy",
                table: "Deployments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignerName",
                table: "Deployments",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SigningKeyFingerprint",
                table: "Deployments",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SigningKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PublicKey = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    Algorithm = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    AddedBy = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsRestricted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastUsedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigningKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SigningKeys_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SigningKeyEnvironments",
                columns: table => new
                {
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SigningKeyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigningKeyEnvironments", x => new { x.SigningKeyId, x.EnvironmentId });
                    table.ForeignKey(
                        name: "FK_SigningKeyEnvironments_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SigningKeyEnvironments_SigningKeys_SigningKeyId",
                        column: x => x.SigningKeyId,
                        principalTable: "SigningKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SigningKeyEnvironments_EnvironmentId",
                table: "SigningKeyEnvironments",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_SigningKeys_AddedBy",
                table: "SigningKeys",
                column: "AddedBy");

            migrationBuilder.CreateIndex(
                name: "IX_SigningKeys_Fingerprint",
                table: "SigningKeys",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SigningKeys_UserId",
                table: "SigningKeys",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SigningKeyEnvironments");

            migrationBuilder.DropTable(
                name: "SigningKeys");

            migrationBuilder.DropColumn(
                name: "RequireImageDigests",
                table: "Stacks");

            migrationBuilder.DropColumn(
                name: "RequireSignedCommits",
                table: "Stacks");

            migrationBuilder.DropColumn(
                name: "ApprovedCommit",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "Endorsement",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "EndorsementProblem",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "SignedBy",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "SignerName",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "SigningKeyFingerprint",
                table: "Deployments");
        }
    }
}
