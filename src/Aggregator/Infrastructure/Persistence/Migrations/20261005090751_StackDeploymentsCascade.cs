using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StackDeploymentsCascade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Deployments_Stacks_StackId",
                table: "Deployments");

            migrationBuilder.AddForeignKey(
                name: "FK_Deployments_Stacks_StackId",
                table: "Deployments",
                column: "StackId",
                principalTable: "Stacks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Deployments_Stacks_StackId",
                table: "Deployments");

            migrationBuilder.AddForeignKey(
                name: "FK_Deployments_Stacks_StackId",
                table: "Deployments",
                column: "StackId",
                principalTable: "Stacks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
