import { useQuery, useSuspenseQuery } from "@tanstack/react-query";
import { type ErrorComponentProps, Link, createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { hasStatus } from "@/api/client";
import { environmentsQuery, gitConnectionsQuery } from "@/api/queries";
import { ErrorText } from "@/components/form-messages";
import { CreateConnectionDialog } from "@/components/git-connections/create-connection-dialog";
import { EditConnectionDialog } from "@/components/git-connections/edit-connection-dialog";
import { LockIcon, LockOpenIcon, WebhookIcon } from "@/components/icons";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";

export const Route = createFileRoute("/_authed/git-connections")({
  loader: ({ context }) => context.queryClient.ensureQueryData(gitConnectionsQuery),
  component: GitConnectionsScreen,
  errorComponent: GitConnectionsLoadError,
});

/**
 * Connections to git servers (ADR-0006): the token the aggregator fetches with, under which URL prefix, and which
 * environments may deploy from there. Administrators manage them; editors add repositories under them.
 */
function GitConnectionsScreen() {
  const { t } = useTranslation();
  const { me } = Route.useRouteContext();
  const { data: connections } = useSuspenseQuery(gitConnectionsQuery);
  const environments = useQuery(environmentsQuery);
  const environmentName = (id: string) => environments.data?.find((e) => e.id === id)?.name ?? id;

  return (
    <div className="grid gap-6">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="font-heading text-2xl font-semibold">{t("gitConnections.title")}</h1>
          <p className="text-muted-foreground mt-1 max-w-3xl">{t("gitConnections.description")}</p>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button variant="outline" asChild>
            <Link to="/repositories">{t("gitConnections.back")}</Link>
          </Button>
          {me.isAdmin && <CreateConnectionDialog />}
        </div>
      </div>

      {connections.length === 0 ? (
        <p className="text-muted-foreground">{t("gitConnections.empty")}</p>
      ) : (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("gitConnections.columns.name")}</TableHead>
              <TableHead>{t("gitConnections.columns.prefix")}</TableHead>
              <TableHead>{t("gitConnections.columns.access")}</TableHead>
              <TableHead>{t("gitConnections.columns.environments")}</TableHead>
              <TableHead>{t("gitConnections.columns.repositories")}</TableHead>
              <TableHead className="sr-only">{t("gitConnections.columns.actions")}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {connections.map((connection) => (
              <TableRow key={connection.id}>
                <TableCell className="font-medium">{connection.name}</TableCell>
                <TableCell className="font-mono text-sm">{connection.urlPrefix}</TableCell>
                <TableCell>
                  <div className="flex flex-wrap gap-1">
                    {connection.hasAccessToken ? (
                      <Badge variant="secondary">
                        <LockIcon data-icon="inline-start" />
                        {t("gitConnections.access.token")}
                      </Badge>
                    ) : (
                      <Badge variant="outline">
                        <LockOpenIcon data-icon="inline-start" />
                        {t("gitConnections.access.anonymous")}
                      </Badge>
                    )}
                    {connection.hasWebhook && (
                      <Badge variant="secondary">
                        <WebhookIcon data-icon="inline-start" />
                        {t("gitConnections.access.webhook")}
                      </Badge>
                    )}
                  </div>
                </TableCell>
                <TableCell className="text-sm">
                  {connection.allEnvironments
                    ? t("gitConnections.environments.all")
                    : connection.environmentIds.length === 0
                      ? t("gitConnections.environments.none")
                      : connection.environmentIds.map(environmentName).join(", ")}
                </TableCell>
                <TableCell className="text-sm">{connection.repositoryCount}</TableCell>
                <TableCell className="text-right">{me.isAdmin && <EditConnectionDialog connection={connection} />}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </div>
  );
}

function GitConnectionsLoadError({ error }: ErrorComponentProps) {
  const { t } = useTranslation();

  return <ErrorText>{hasStatus(error, 403) ? t("gitConnections.forbidden") : t("gitConnections.loadFailed")}</ErrorText>;
}
