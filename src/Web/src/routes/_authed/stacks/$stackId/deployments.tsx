import { useSuspenseQuery } from "@tanstack/react-query";
import { type ErrorComponentProps, Link, createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { z } from "zod";
import { hasStatus } from "@/api/client";
import { deploymentsQuery, stackQuery } from "@/api/queries";
import { ErrorText } from "@/components/form-messages";
import { ArrowLeftIcon, Loader2Icon, RefreshCwIcon } from "@/components/icons";
import { DeploymentStatusBadge } from "@/components/stacks/deployment-status-badge";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import {
  DEPLOYMENT_POLL_MS,
  type Deployment,
  durationSeconds,
  hasUnfinished,
  isFinished,
  shortCommit,
  splitDuration,
} from "@/features/stacks/model";
import { formatDateTime } from "@/lib/format";
import { rowClass, rowLinkClass } from "@/lib/row-link";
import { useLiveStatus } from "@/components/live/hub-provider";
import { pollingInterval } from "@/features/live/model";

const statusFilters = ["all", "unfinished", "Succeeded", "Failed", "Cancelled"] as const;
type StatusFilter = (typeof statusFilters)[number];

const searchSchema = z.object({
  status: z.enum(statusFilters).optional().catch(undefined),
});

export const Route = createFileRoute("/_authed/stacks/$stackId/deployments")({
  validateSearch: searchSchema,
  loader: ({ context, params }) =>
    Promise.all([
      context.queryClient.ensureQueryData(stackQuery(params.stackId)),
      context.queryClient.ensureQueryData(deploymentsQuery(params.stackId)),
    ]),
  component: DeploymentHistoryScreen,
  errorComponent: HistoryLoadError,
});

function matches(deployment: Deployment, filter: StatusFilter): boolean {
  return filter === "all" || (filter === "unfinished" ? !isFinished(deployment.status) : deployment.status === filter);
}

/** Every recent deployment of one stack, newest first, with a status filter kept in the URL. */
function DeploymentHistoryScreen() {
  const { t, i18n } = useTranslation();
  const { stackId } = Route.useParams();
  const { status = "all" } = Route.useSearch();
  const navigate = Route.useNavigate();
  const { data: stack } = useSuspenseQuery(stackQuery(stackId));
  const live = useLiveStatus();
  const { data: deployments, isFetching, refetch } = useSuspenseQuery({
    ...deploymentsQuery(stackId),
    refetchInterval: (query) =>
      query.state.data && hasUnfinished(query.state.data) ? pollingInterval(live, DEPLOYMENT_POLL_MS) : false,
  });
  const shown = deployments.filter((d) => matches(d, status));
  const now = new Date();

  const duration = (deployment: Deployment) => {
    const seconds = durationSeconds(deployment, now);
    return seconds === null ? "—" : t("deployments.duration", splitDuration(seconds));
  };

  return (
    <div className="grid gap-6">
      <div className="grid gap-3">
        <Link
          to="/stacks/$stackId"
          params={{ stackId }}
          className="text-muted-foreground hover:text-foreground flex w-fit items-center gap-1 text-sm"
        >
          <ArrowLeftIcon className="size-4" />
          {stack.name}
        </Link>
        <h1 className="font-heading text-2xl font-semibold">{t("deployments.title", { name: stack.name })}</h1>
      </div>

      <div className="flex flex-wrap items-end gap-3">
        <div className="grid gap-1.5">
          <Label htmlFor="deployment-status">{t("deployments.filter")}</Label>
          <Select
            value={status}
            onValueChange={(value) =>
              value !== "" &&
              void navigate({ search: { status: value === "all" ? undefined : (value as StatusFilter) }, replace: true })
            }
          >
            <SelectTrigger id="deployment-status" className="min-w-44">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {statusFilters.map((filter) => (
                <SelectItem key={filter} value={filter}>
                  {t(`deployments.filters.${filter}`)}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <Button variant="outline" disabled={isFetching} onClick={() => void refetch()}>
          {isFetching ? <Loader2Icon data-icon="inline-start" className="animate-spin" /> : <RefreshCwIcon data-icon="inline-start" />}
          {t("stacks.refresh")}
        </Button>
      </div>

      {deployments.length === 0 ? (
        <p className="text-muted-foreground">{t("deployments.empty")}</p>
      ) : (
        <>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("deployments.columns.status")}</TableHead>
                <TableHead>{t("deployments.columns.queuedAt")}</TableHead>
                <TableHead className="hidden sm:table-cell">{t("deployments.columns.commit")}</TableHead>
                <TableHead className="hidden md:table-cell">{t("deployments.columns.kind")}</TableHead>
                <TableHead className="hidden lg:table-cell">{t("deployments.columns.duration")}</TableHead>
                <TableHead className="hidden lg:table-cell">{t("deployments.columns.error")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {shown.map((deployment) => (
                <TableRow key={deployment.id} className={rowClass}>
                  <TableCell>
                    <DeploymentStatusBadge status={deployment.status} />
                  </TableCell>
                  <TableCell className="whitespace-nowrap">
                    <Link to="/deployments/$deploymentId" params={{ deploymentId: deployment.id }} className={rowLinkClass}>
                      {formatDateTime(deployment.queuedAt, i18n.resolvedLanguage)}
                    </Link>
                  </TableCell>
                  <TableCell className="hidden text-sm sm:table-cell">
                    <span className="font-mono">{shortCommit(deployment.commit) ?? "—"}</span>
                    {deployment.endorsement !== "NotChecked" && (
                      <div className="text-muted-foreground text-xs" title={t("deployments.columns.endorsement")}>
                        {t(`deployment.endorsement.status.${deployment.endorsement}`, { name: deployment.signedBy ?? "—" })}
                      </div>
                    )}
                  </TableCell>
                  <TableCell className="hidden text-sm md:table-cell">
                    {t(`deployments.trigger.${deployment.trigger}`)}
                    {deployment.pullPolicy === "Always" && (
                      <div className="text-muted-foreground text-xs">{t("deployments.withPull")}</div>
                    )}
                  </TableCell>
                  <TableCell className="hidden text-sm whitespace-nowrap lg:table-cell">{duration(deployment)}</TableCell>
                  <TableCell className="text-destructive hidden max-w-72 truncate text-sm lg:table-cell" title={deployment.error ?? undefined} lang="en">
                    {deployment.error}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
          <p className="text-muted-foreground text-sm">
            {shown.length === 0 ? t("deployments.noMatches") : t("deployments.count", { shown: shown.length, total: deployments.length })}
          </p>
        </>
      )}
    </div>
  );
}

function HistoryLoadError({ error }: ErrorComponentProps) {
  const { t } = useTranslation();

  return <ErrorText>{hasStatus(error, 404) ? t("stack.notFound") : t("deployments.loadFailed")}</ErrorText>;
}
