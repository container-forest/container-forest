import { useSuspenseQuery } from "@tanstack/react-query";
import { Link, createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { z } from "zod";
import { environmentsQuery } from "@/api/queries";
import { ArrowLeftIcon } from "@/components/icons";
import { ContainerTabs } from "@/components/metrics/container-tabs";
import { ContainerMetricsView, RangePicker } from "@/components/metrics/metrics-views";
import { Button } from "@/components/ui/button";
import { metricsRanges } from "@/features/metrics/model";

const searchSchema = z.object({ range: z.enum(metricsRanges).optional().catch(undefined) });

export const Route = createFileRoute("/_authed/containers_/$environmentId/$containerId/metrics")({
  validateSearch: searchSchema,
  loader: ({ context }) => context.queryClient.ensureQueryData(environmentsQuery),
  component: ContainerMetricsScreen,
});

/** One container's resource usage over a range; history stays readable after the container is gone. */
function ContainerMetricsScreen() {
  const { t } = useTranslation();
  const { environmentId, containerId } = Route.useParams();
  const range = Route.useSearch().range ?? "1h";
  const navigate = Route.useNavigate();
  const { data: environments } = useSuspenseQuery(environmentsQuery);
  const environment = environments.find((e) => e.id === environmentId);

  return (
    <div className="grid gap-6">
      <div>
        <Button asChild variant="ghost" size="sm" className="-ml-2">
          <Link to="/containers" search={{ environment: environmentId }}>
            <ArrowLeftIcon data-icon="inline-start" />
            {t("containerLogs.back")}
          </Link>
        </Button>
        <h1 className="font-heading mt-2 text-2xl font-semibold break-all">{t("metrics.containerTitle", { name: containerId })}</h1>
        {environment && <p className="text-muted-foreground mt-1">{t("metrics.containerDescription", { environment: environment.name })}</p>}
      </div>
      <ContainerTabs environmentId={environmentId} containerId={containerId} active="metrics" />
      {environment === undefined ? (
        <p role="alert" className="text-destructive">
          {t("containerLogs.errors.environmentNotFound")}
        </p>
      ) : (
        <>
          <RangePicker value={range} onChange={(next) => void navigate({ search: { range: next }, replace: true })} />
          <ContainerMetricsView environmentId={environmentId} container={containerId} range={range} />
        </>
      )}
    </div>
  );
}
