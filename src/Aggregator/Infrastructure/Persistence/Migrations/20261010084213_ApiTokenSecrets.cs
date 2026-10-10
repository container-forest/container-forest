using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApiTokenSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SecretsRequested",
                table: "CliAuthorizations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TokenCanDecryptSecrets",
                table: "CliAuthorizations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanDecryptSecrets",
                table: "ApiTokens",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecretsRequested",
                table: "CliAuthorizations");

            migrationBuilder.DropColumn(
                name: "TokenCanDecryptSecrets",
                table: "CliAuthorizations");

            migrationBuilder.DropColumn(
                name: "CanDecryptSecrets",
                table: "ApiTokens");
        }
    }
}
