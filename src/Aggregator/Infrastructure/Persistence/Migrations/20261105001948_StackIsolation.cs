using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StackIsolation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeployedComposeFacts",
                table: "Stacks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PolicyAllowances",
                table: "Stacks",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<Guid>(
                name: "StackId",
                table: "AgeKeys",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ComposePolicies",
                columns: table => new
                {
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DataRoot = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SystemReadOnlyPaths = table.Column<string>(type: "TEXT", nullable: false),
                    Allowances = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComposePolicies", x => x.EnvironmentId);
                    table.ForeignKey(
                        name: "FK_ComposePolicies_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgeKeys_StackId",
                table: "AgeKeys",
                column: "StackId");

            migrationBuilder.AddForeignKey(
                name: "FK_AgeKeys_Stacks_StackId",
                table: "AgeKeys",
                column: "StackId",
                principalTable: "Stacks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgeKeys_Stacks_StackId",
                table: "AgeKeys");

            migrationBuilder.DropTable(
                name: "ComposePolicies");

            migrationBuilder.DropIndex(
                name: "IX_AgeKeys_StackId",
                table: "AgeKeys");

            migrationBuilder.DropColumn(
                name: "DeployedComposeFacts",
                table: "Stacks");

            migrationBuilder.DropColumn(
                name: "PolicyAllowances",
                table: "Stacks");

            migrationBuilder.DropColumn(
                name: "StackId",
                table: "AgeKeys");
        }
    }
}
