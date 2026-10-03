using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContainerForest.Aggregator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MoveRepositoryTokensToConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-0006: every repository with a token gets a connection of its own, with the same id and name, its URL
            // as the prefix and the environments that already have its stacks. Nothing changes for those stacks; an
            // administrator can merge such connections into one per organization later.
            migrationBuilder.Sql(
                """
                INSERT INTO "GitConnections" ("Id", "Name", "UrlPrefix", "ProtectedAccessToken", "AccessUserName", "AllowsAllEnvironments", "ProtectedWebhookSecret", "CreatedAt")
                SELECT "Id", "Name", "Url", "ProtectedAccessToken", "AccessUserName", 0, NULL, "CreatedAt"
                FROM "Repositories" WHERE "ProtectedAccessToken" IS NOT NULL;

                INSERT INTO "GitConnectionEnvironments" ("GitConnectionId", "EnvironmentId")
                SELECT DISTINCT s."RepositoryId", s."EnvironmentId"
                FROM "Stacks" AS s JOIN "Repositories" AS r ON r."Id" = s."RepositoryId"
                WHERE r."ProtectedAccessToken" IS NOT NULL;

                UPDATE "Repositories" SET "ConnectionId" = "Id" WHERE "ProtectedAccessToken" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Each repository takes the token of its connection back; the connections themselves go.
            migrationBuilder.Sql(
                """
                UPDATE "Repositories" SET
                    "ProtectedAccessToken" = (SELECT c."ProtectedAccessToken" FROM "GitConnections" AS c WHERE c."Id" = "Repositories"."ConnectionId"),
                    "AccessUserName" = (SELECT c."AccessUserName" FROM "GitConnections" AS c WHERE c."Id" = "Repositories"."ConnectionId"),
                    "ConnectionId" = NULL
                WHERE "ConnectionId" IS NOT NULL;

                DELETE FROM "GitConnectionEnvironments";
                DELETE FROM "GitConnections";
                """);
        }
    }
}
