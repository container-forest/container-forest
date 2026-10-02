import { useQuery, useSuspenseQuery } from "@tanstack/react-query";
import { Link, createFileRoute, redirect } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { environmentsQuery, stacksQuery } from "@/api/queries";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon, RefreshCwIcon } from "@/components/icons";
import { CreateStackDialog } from "@/components/stacks/create-stack-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import type { Environment } from "@/features/containers/model";
import { describeError, shortCommit, stacksSearchSchema } from "@/features/stacks/model";
import { formatDateTime } from "@/lib/format";

export const Route = createFileRoute("/_authed/stacks/")({
  validateSearch: stacksSearchSchema,
  beforeLoad: async ({ context, search }) => {
    const environments = await context.queryClient.ensureQueryData(environmentsQuery);
    // No environment in the URL, or one the user cannot see (any more): open the first one.
    const first = environments[0];
    if (first !== undefined && !environments.some((e) => e.id === search.environment)) {
      throw redirect({ to: "/stacks", search: { environment: first.id }, replace: true });
    }
  },
  component: StacksScreen,
  errorComponent: StacksLoadError,
});

/** The stacks of one environment: source, deployed commit, and "new stack" for editors of the environment. */
function StacksScreen() {
  const { t } = useTranslation();
  const search = Route.useSearch();
  const navigate = Route.useNavigate();
  const { me } = Route.useRouteContext();
  const { data: environments } = useSuspenseQuery(environmentsQuery);
  const environment = environments.find((e) => e.id === search.environment);

  return (
    <div className="grid gap-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="font-heading text-2xl font-semibold">{t("stacks.title")}</h1>
          <p className="text-muted-foreground mt-1">{t("stacks.description")}</p>
        </div>
        {environments.length > 0 && (
          <div className="flex items-end gap-3">
            <div className="grid gap-1.5">
              <Label htmlFor="stacks-environment">{t("stacks.environment")}</Label>
              <Select
                value={search.environment}
                onValueChange={(id) =>
                  id !== "" && id !== search.environment && void navigate({ search: { environment: id }, replace: true })
                }
              >
                <SelectTrigger id="stacks-environment" className="min-w-56">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {environments.map((e) => (
                    <SelectItem key={e.id} value={e.id}>
                      {e.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            {/* Adding a stack takes Editor on the environment; hiding is cosmetic, the aggregator answers 403 otherwise. */}
            {environment && (me.isAdmin || environment.role === "Editor") && (
              <CreateStackDialog environmentId={environment.id} />
            )}
          </div>
        )}
      </div>

      {environment === undefined ? (
        <p className="text-muted-foreground">{t("stacks.noEnvironments")}</p>
      ) : (
        <EnvironmentStacks key={environment.id} environment={environment} />
      )}
    </div>
  );
}

function EnvironmentStacks({ environment }: { environment: Environment }) {
  const { t, i18n } = useTranslation();
  const stacks = useQuery(stacksQuery(environment.id));

  if (stacks.isPending) {
    return (
      <p className="text-muted-foreground flex items-center gap-2">
        <Loader2Icon className="animate-spin" />
        {t("stacks.loading")}
      </p>
    );
  }

  if (stacks.isError) {
    const { key, detail } = describeError(stacks.error);
    return (
      <ErrorText>
        {t("stacks.loadFailed")} {t(key)} {detail}
      </ErrorText>
    );
  }

  if (stacks.data.length === 0) {
    return <p className="text-muted-foreground">{t("stacks.empty", { name: environment.name })}</p>;
  }

  return (
    <div className="grid gap-3">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{t("stacks.columns.name")}</TableHead>
            <TableHead>{t("stacks.columns.source")}</TableHead>
            <TableHead>{t("stacks.columns.deployed")}</TableHead>
            <TableHead>{t("stacks.columns.access")}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {stacks.data.map((stack) => (
            <TableRow key={stack.id}>
              <TableCell>
                <Link to="/stacks/$stackId" params={{ stackId: stack.id }} className="font-medium hover:underline">
                  {stack.name}
                </Link>
                {stack.isSystem && (
                  <Badge variant="outline" className="ml-2">
                    {t("stacks.system")}
                  </Badge>
                )}
              </TableCell>
              <TableCell className="text-sm">
                <div>{stack.repositoryName ?? t("stacks.repositoryGone")}</div>
                <div className="text-muted-foreground font-mono text-xs">
                  {stack.branch} · {stack.composePath}
                </div>
              </TableCell>
              <TableCell className="text-sm">
                {stack.deployedCommit && stack.deployedAt ? (
                  <>
                    <span className="font-mono">{shortCommit(stack.deployedCommit)}</span>
                    <div className="text-muted-foreground text-xs">{formatDateTime(stack.deployedAt, i18n.resolvedLanguage)}</div>
                  </>
                ) : (
                  <span className="text-muted-foreground">{t("stacks.notDeployed")}</span>
                )}
              </TableCell>
              <TableCell className="text-muted-foreground text-sm">
                {stack.canDeploy ? t("stacks.access.deploy") : stack.canRestart ? t("stacks.access.restart") : t("stacks.access.view")}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <div>
        <Button variant="outline" size="sm" disabled={stacks.isFetching} onClick={() => void stacks.refetch()}>
          {stacks.isFetching ? <Loader2Icon data-icon="inline-start" className="animate-spin" /> : <RefreshCwIcon data-icon="inline-start" />}
          {t("stacks.refresh")}
        </Button>
      </div>
    </div>
  );
}

function StacksLoadError() {
  const { t } = useTranslation();

  return <ErrorText>{t("stacks.loadEnvironmentsFailed")}</ErrorText>;
}
