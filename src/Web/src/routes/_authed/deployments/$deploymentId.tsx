import { useQuery, useSuspenseQuery } from "@tanstack/react-query";
import { type ErrorComponentProps, Link, createFileRoute } from "@tanstack/react-router";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { hasStatus } from "@/api/client";
import { deploymentQuery, stackQuery } from "@/api/queries";
import { ErrorText } from "@/components/form-messages";
import { ArrowLeftIcon } from "@/components/icons";
import { DeploymentStatusBadge } from "@/components/stacks/deployment-status-badge";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Label } from "@/components/ui/label";
import {
  DEPLOYMENT_POLL_MS,
  type DeploymentDetails,
  type DeploymentLogLine,
  durationSeconds,
  isFinished,
  shortCommit,
  splitDuration,
} from "@/features/stacks/model";
import { formatDateTime } from "@/lib/format";
import { cn } from "@/lib/utils";

export const Route = createFileRoute("/_authed/deployments/$deploymentId")({
  loader: ({ context, params }) => context.queryClient.ensureQueryData(deploymentQuery(params.deploymentId)),
  component: DeploymentScreen,
  errorComponent: DeploymentLoadError,
});

/** One deployment: its outcome and log, followed until it finishes. */
function DeploymentScreen() {
  const { t, i18n } = useTranslation();
  const { deploymentId } = Route.useParams();
  const { data: deployment } = useSuspenseQuery({
    ...deploymentQuery(deploymentId),
    refetchInterval: (query) => (query.state.data && !isFinished(query.state.data.status) ? DEPLOYMENT_POLL_MS : false),
  });
  const stack = useQuery(stackQuery(deployment.stackId));
  const language = i18n.resolvedLanguage;
  const seconds = durationSeconds(deployment, new Date());

  return (
    <div className="grid max-w-5xl gap-6">
      <div className="grid gap-3">
        <Link
          to="/stacks/$stackId/deployments"
          params={{ stackId: deployment.stackId }}
          className="text-muted-foreground hover:text-foreground flex w-fit items-center gap-1 text-sm"
        >
          <ArrowLeftIcon className="size-4" />
          {t("deployment.back", { name: stack.data?.name ?? "…" })}
        </Link>
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="font-heading text-2xl font-semibold">
            {t("deployment.title", { date: formatDateTime(deployment.queuedAt, language) })}
          </h1>
          <DeploymentStatusBadge status={deployment.status} />
        </div>
      </div>

      <Card>
        <CardContent>
          <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-[auto_1fr]">
            <dt className="text-muted-foreground">{t("deployments.columns.commit")}</dt>
            <dd className="font-mono" title={deployment.commit ?? undefined}>
              {shortCommit(deployment.commit) ?? "—"}
            </dd>
            <dt className="text-muted-foreground">{t("deployments.columns.kind")}</dt>
            <dd>
              {t(`deployments.trigger.${deployment.trigger}`)}
              {deployment.pullPolicy === "Always" && `, ${t("deployments.withPull")}`}
            </dd>
            <dt className="text-muted-foreground">{t("deployment.startedAt")}</dt>
            <dd>{deployment.startedAt ? formatDateTime(deployment.startedAt, language) : "—"}</dd>
            <dt className="text-muted-foreground">{t("deployment.finishedAt")}</dt>
            <dd>{deployment.finishedAt ? formatDateTime(deployment.finishedAt, language) : "—"}</dd>
            <dt className="text-muted-foreground">{t("deployments.columns.duration")}</dt>
            <dd>{seconds === null ? "—" : t("deployments.duration", splitDuration(seconds))}</dd>
          </dl>
          {deployment.error && (
            <p role="alert" className="text-destructive mt-4 text-sm" lang="en">
              {deployment.error}
            </p>
          )}
        </CardContent>
      </Card>

      <LogCard deployment={deployment} />
    </div>
  );
}

const levelClasses: Record<DeploymentLogLine["level"], string> = {
  Info: "",
  Warning: "text-amber-600 dark:text-amber-400",
  Error: "text-destructive",
};

/** The pipeline's and compose's lines; while the deployment runs, the view keeps to the newest line unless told not to. */
function LogCard({ deployment }: { deployment: DeploymentDetails }) {
  const { t, i18n } = useTranslation();
  const [problemsOnly, setProblemsOnly] = useState(false);
  const [follow, setFollow] = useState(true);
  const end = useRef<HTMLDivElement>(null);
  const lines = problemsOnly ? deployment.log.filter((line) => line.level !== "Info") : deployment.log;
  const timeFormat = new Intl.DateTimeFormat(i18n.resolvedLanguage, { timeStyle: "medium" });

  useEffect(() => {
    if (follow && !isFinished(deployment.status)) {
      end.current?.scrollIntoView({ block: "nearest" });
    }
  }, [follow, deployment.status, deployment.log.length]);

  return (
    <Card>
      <CardHeader className="flex flex-wrap items-center justify-between gap-3">
        <CardTitle>{t("deployment.log.title")}</CardTitle>
        <div className="flex flex-wrap gap-4">
          <div className="flex items-center gap-2">
            <Checkbox id="log-problems" checked={problemsOnly} onCheckedChange={(checked) => setProblemsOnly(checked === true)} />
            <Label htmlFor="log-problems">{t("deployment.log.problemsOnly")}</Label>
          </div>
          {!isFinished(deployment.status) && (
            <div className="flex items-center gap-2">
              <Checkbox id="log-follow" checked={follow} onCheckedChange={(checked) => setFollow(checked === true)} />
              <Label htmlFor="log-follow">{t("deployment.log.follow")}</Label>
            </div>
          )}
        </div>
      </CardHeader>
      <CardContent>
        {lines.length === 0 ? (
          <p className="text-muted-foreground text-sm">{deployment.log.length === 0 ? t("deployment.log.empty") : t("deployment.log.noProblems")}</p>
        ) : (
          <div className="bg-muted max-h-[32rem] overflow-auto rounded-md p-3 font-mono text-xs" lang="en">
            {lines.map((line, index) => (
              <div key={index} className={cn("grid grid-cols-[auto_1fr] gap-3", levelClasses[line.level])}>
                <span className="text-muted-foreground">{timeFormat.format(new Date(line.at))}</span>
                <span className="whitespace-pre-wrap break-all">{line.text}</span>
              </div>
            ))}
            <div ref={end} />
          </div>
        )}
      </CardContent>
    </Card>
  );
}

function DeploymentLoadError({ error }: ErrorComponentProps) {
  const { t } = useTranslation();

  return <ErrorText>{hasStatus(error, 404) ? t("deployment.notFound") : t("deployment.loadFailed")}</ErrorText>;
}
