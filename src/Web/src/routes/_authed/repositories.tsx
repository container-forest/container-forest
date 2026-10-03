import { useSuspenseQuery } from "@tanstack/react-query";
import { type ErrorComponentProps, createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { z } from "zod";
import { hasStatus } from "@/api/client";
import { repositoriesQuery } from "@/api/queries";
import { ErrorText } from "@/components/form-messages";
import { LockIcon, LockOpenIcon, SearchIcon } from "@/components/icons";
import { AddRepositoryDialog } from "@/components/stacks/add-repository-dialog";
import { EditRepositoryDialog } from "@/components/stacks/edit-repository-dialog";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { formatDate } from "@/lib/format";

const searchSchema = z.object({
  q: z.string().optional().catch(undefined),
});

export const Route = createFileRoute("/_authed/repositories")({
  validateSearch: searchSchema,
  loader: ({ context }) => context.queryClient.ensureQueryData(repositoriesQuery),
  component: RepositoriesScreen,
  errorComponent: RepositoriesLoadError,
});

/** Git repositories stacks are deployed from. Editors see them to choose one; administrators add them. */
function RepositoriesScreen() {
  const { t, i18n } = useTranslation();
  const { me } = Route.useRouteContext();
  const { q = "" } = Route.useSearch();
  const navigate = Route.useNavigate();
  const { data: repositories } = useSuspenseQuery(repositoriesQuery);
  const text = q.trim().toLowerCase();
  const shown = repositories.filter((r) => text === "" || r.name.toLowerCase().includes(text) || r.url.toLowerCase().includes(text));

  return (
    <div className="grid gap-6">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="font-heading text-2xl font-semibold">{t("repositories.title")}</h1>
          <p className="text-muted-foreground mt-1">{t("repositories.description")}</p>
        </div>
        {me.isAdmin && <AddRepositoryDialog />}
      </div>

      <div className="grid max-w-sm gap-1.5">
        <Label htmlFor="repository-search">{t("repositories.search")}</Label>
        <div className="relative">
          <SearchIcon className="text-muted-foreground pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2" />
          <Input
            id="repository-search"
            type="search"
            className="pl-8"
            value={q}
            onChange={(event) => void navigate({ search: { q: event.target.value || undefined }, replace: true })}
          />
        </div>
      </div>

      {shown.length === 0 ? (
        <p className="text-muted-foreground">{repositories.length === 0 ? t("repositories.empty") : t("repositories.noMatches")}</p>
      ) : (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("repositories.columns.name")}</TableHead>
              <TableHead>{t("repositories.columns.url")}</TableHead>
              <TableHead>{t("repositories.columns.access")}</TableHead>
              <TableHead>{t("repositories.columns.createdAt")}</TableHead>
              {me.isAdmin && <TableHead className="sr-only">{t("repositories.columns.actions")}</TableHead>}
            </TableRow>
          </TableHeader>
          <TableBody>
            {shown.map((repository) => (
              <TableRow key={repository.id}>
                <TableCell className="font-medium">{repository.name}</TableCell>
                <TableCell className="font-mono text-sm">{repository.url}</TableCell>
                <TableCell>
                  {repository.hasAccessToken ? (
                    <Badge variant="secondary">
                      <LockIcon data-icon="inline-start" />
                      {t("repositories.access.token")}
                    </Badge>
                  ) : (
                    <Badge variant="outline">
                      <LockOpenIcon data-icon="inline-start" />
                      {t("repositories.access.public")}
                    </Badge>
                  )}
                </TableCell>
                <TableCell className="text-sm">{formatDate(repository.createdAt, i18n.resolvedLanguage)}</TableCell>
                {me.isAdmin && (
                  <TableCell className="text-right">
                    <EditRepositoryDialog repository={repository} />
                  </TableCell>
                )}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </div>
  );
}

function RepositoriesLoadError({ error }: ErrorComponentProps) {
  const { t } = useTranslation();

  return <ErrorText>{hasStatus(error, 403) ? t("repositories.forbidden") : t("repositories.loadFailed")}</ErrorText>;
}
