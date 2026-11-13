import { useQuery, useSuspenseQuery } from "@tanstack/react-query";
import { Link, createFileRoute, redirect } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { environmentsQuery, stacksQuery } from "@/api/queries";
import { isEditorRole } from "@/api/users";
import { ErrorText } from "@/components/form-messages";
import { Loader2Icon, RefreshCwIcon } from "@/components/icons";
import { CreateStackDialog } from "@/components/stacks/create-stack-dialog";
import { UnmanagedStacksSection } from "@/components/stacks/unmanaged-stacks-section";
import { type StackQuickResult, StackQuickActions } from "@/components/stacks/stack-quick-actions";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import type { Environment } from "@/features/containers/model";
import { describeError, shortCommit, stacksSearchSchema } from "@/features/stacks/model";
import { formatDateTime } from "@/lib/format";
import { rowActionsClass, rowClass, rowLinkClass } from "@/lib/row-link";
import { cn } from "@/lib/utils";

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
  const { me } = Route.useRouteContext();
  const { data: environments } = useSuspenseQuery(environmentsQuery);
  const environment = environments.find((e) => e.id === search.environment);

  return (
    <div className="grid gap-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="font-heading flex flex-wrap items-baseline gap-x-3 text-2xl font-semibold">
            {t("stacks.title")}
            {environment && <span className="text-muted-foreground text-lg font-medium">{environment.name}</span>}
          </h1>
          <p className="text-muted-foreground mt-1">{t("stacks.description")}</p>
        </div>
        {/* Adding a stack takes Editor on the environment; hiding is cosmetic, the aggregator answers 403 otherwise. */}
        {environment && (me.isAdmin || isEditorRole(environment.role)) && <CreateStackDialog environmentId={environment.id} />}
      </div>

      {environment === undefined ? (
        <p className="text-muted-foreground">{t("stacks.noEnvironments")}</p>
      ) : (
        <div key={environment.id} className="grid gap-8">
          <EnvironmentStacks environment={environment} />
          <UnmanagedStacksSection environment={environment} />
        </div>
      )}
    </div>
  );
}

function EnvironmentStacks({ environment }: { environment: Environment }) {
  const { t, i18n } = useTranslation();
  const stacks = useQuery(stacksQuery(environment.id));
  const [result, setResult] = useState<StackQuickResult>();

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
      <QuickResult result={result} />
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{t("stacks.columns.name")}</TableHead>
            <TableHead className="hidden md:table-cell">{t("stacks.columns.source")}</TableHead>
            <TableHead>{t("stacks.columns.deployed")}</TableHead>
            <TableHead className="hidden lg:table-cell">{t("stacks.columns.access")}</TableHead>
            <TableHead className="text-right">
              <span className="sr-only sm:not-sr-only">{t("stacks.columns.actions")}</span>
            </TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {stacks.data.map((stack) => (
            <TableRow key={stack.id} className={rowClass}>
              <TableCell className="whitespace-normal">
                <Link to="/stacks/$stackId" params={{ stackId: stack.id }} className={cn("font-medium", rowLinkClass)}>
                  {stack.name}
                </Link>
                {stack.isSystem && (
                  <Badge variant="outline" className="ml-2">
                    {t("stacks.system")}
                  </Badge>
                )}
                <div className="text-muted-foreground text-xs md:hidden">{stack.repositoryName ?? t("stacks.repositoryGone")}</div>
              </TableCell>
              <TableCell className="hidden text-sm md:table-cell">
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
              <TableCell className="text-muted-foreground hidden text-sm lg:table-cell">
                {stack.canDeploy ? t("stacks.access.deploy") : stack.canRestart ? t("stacks.access.restart") : t("stacks.access.view")}
              </TableCell>
              <TableCell className={cn("text-right", rowActionsClass)}>
                <StackQuickActions stack={stack} onResult={setResult} />
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

/** The outcome of the last quick action, above the list; a deployment links to its log. */
function QuickResult({ result }: { result: StackQuickResult | undefined }) {
  const { t } = useTranslation();

  return (
    <div aria-live="polite">
      {result?.kind === "deployed" && (
        <p role="status" className="text-sm">
          {t("stacks.quick.deployQueued", { name: result.stack })}{" "}
          <Link to="/deployments/$deploymentId" params={{ deploymentId: result.deploymentId }} className="font-medium underline">
            {t("stacks.quick.openDeployment")}
          </Link>
        </p>
      )}
      {result?.kind === "done" && (
        <p role="status" className="text-sm">
          {t(`stacks.quick.done.${result.action}`, { name: result.stack })}
        </p>
      )}
      {result?.kind === "composeFailed" && (
        <ErrorText>
          {t("stacks.quick.composeFailed", { name: result.stack })} {result.error}
        </ErrorText>
      )}
      {result?.kind === "error" && (
        <ErrorText>
          {result.stack}: {t(result.key)} {result.detail}
        </ErrorText>
      )}
    </div>
  );
}

function StacksLoadError() {
  const { t } = useTranslation();

  return <ErrorText>{t("stacks.loadEnvironmentsFailed")}</ErrorText>;
}
