using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImageUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ImageHookAllowsDowngrade",
                table: "Stacks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ImageHookTokenHash",
                table: "Stacks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedPushToken",
                table: "GitConnections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PushUserName",
                table: "GitConnections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ImageUpdateId",
                table: "Deployments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AggregatorSigningKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProtectedPrivateKey = table.Column<string>(type: "TEXT", nullable: false),
                    PublicKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RetiredAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AggregatorSigningKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImagePolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Image = table.Column<string>(type: "TEXT", maxLength: 263, nullable: false),
                    TagPattern = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Order = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Services = table.Column<string>(type: "TEXT", nullable: false),
                    Poll = table.Column<bool>(type: "INTEGER", nullable: false),
                    AcceptRequests = table.Column<bool>(type: "INTEGER", nullable: false),
                    PinDigest = table.Column<bool>(type: "INTEGER", nullable: false),
                    PinnedTag = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PinnedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PinnedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastChangedBy = table.Column<Guid>(type: "TEXT", nullable: false),
                    LastChangedByAdministrator = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastCheckedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    FailureNotified = table.Column<bool>(type: "INTEGER", nullable: false),
                    RegistryFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    Warning = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LatestTag = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImagePolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImagePolicies_Stacks_StackId",
                        column: x => x.StackId,
                        principalTable: "Stacks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImageTagCache",
                columns: table => new
                {
                    Image = table.Column<string>(type: "TEXT", maxLength: 263, nullable: false),
                    Tag = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Digest = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Created = table.Column<long>(type: "INTEGER", nullable: true),
                    FetchedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageTagCache", x => new { x.Image, x.Tag });
                });

            migrationBuilder.CreateTable(
                name: "ImageUpdateRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PolicyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Commit = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Parent = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ComposePath = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Tag = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Digest = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RequestedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    RequestedByName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Changes = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageUpdateRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImageUpdateRecords_Stacks_StackId",
                        column: x => x.StackId,
                        principalTable: "Stacks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AggregatorSigningKeys_Fingerprint",
                table: "AggregatorSigningKeys",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImagePolicies_StackId_Image",
                table: "ImagePolicies",
                columns: new[] { "StackId", "Image" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageUpdateRecords_StackId_Commit",
                table: "ImageUpdateRecords",
                columns: new[] { "StackId", "Commit" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AggregatorSigningKeys");

            migrationBuilder.DropTable(
                name: "ImagePolicies");

            migrationBuilder.DropTable(
                name: "ImageTagCache");

            migrationBuilder.DropTable(
                name: "ImageUpdateRecords");

            migrationBuilder.DropColumn(
                name: "ImageHookAllowsDowngrade",
                table: "Stacks");

            migrationBuilder.DropColumn(
                name: "ImageHookTokenHash",
                table: "Stacks");

            migrationBuilder.DropColumn(
                name: "ProtectedPushToken",
                table: "GitConnections");

            migrationBuilder.DropColumn(
                name: "PushUserName",
                table: "GitConnections");

            migrationBuilder.DropColumn(
                name: "ImageUpdateId",
                table: "Deployments");
        }
    }
}
