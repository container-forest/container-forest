using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WebhookDeploy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DeployOnPush",
                table: "Stacks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedWebhookSecret",
                table: "Repositories",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeployOnPush",
                table: "Stacks");

            migrationBuilder.DropColumn(
                name: "ProtectedWebhookSecret",
                table: "Repositories");
        }
    }
}
