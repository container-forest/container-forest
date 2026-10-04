using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StackRestartOnFileChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RestartOnFileChange",
                table: "Stacks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RestartOnFileChange",
                table: "Stacks");
        }
    }
}
