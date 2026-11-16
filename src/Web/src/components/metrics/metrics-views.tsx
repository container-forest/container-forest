import { type UseQueryResult, useQuery } from "@tanstack/react-query";
import { cn } from "cn";
import { useTranslation } from "react-i18next";
import {
  type MetricsRange,
  containerMetricsQuery,
  currentMetricsQuery,
  environmentMetricsQuery,
  stackMetricsQuery,
  unmanagedStackMetricsQuery,
} from "@/api/queries";
import { Loader2Icon } from "@/components/icons";
import { type ChartSeries, MetricChart } from "@/components/metrics/metric-chart";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import {
  type ContainerSeries,
  type MetricsWindow,
  type Num,
  type StackMetrics,
  formatBytes,
  formatCount,
  formatPercent,
  formatRate,
  last,
  metricsRanges,
  peak,
  sendsNoMetrics,
  stackRows,
  toRows,
} from "@/features/metrics/model";

/** The ranges as one segmented control; the filter row above every metrics view. */
export function RangePicker({ value, onChange }: { value: MetricsRange; onChange: (range: MetricsRange) => void }) {
  const { t } = useTranslation();
  return (
    <div role="radiogroup" aria-label={t("metrics.range")} className="bg-muted inline-flex w-fit rounded-lg p-0.5">
      {metricsRanges.map((range) => (
        <button
          key={range}
          type="button"
          role="radio"
          aria-checked={range === value}
          onClick={() => onChange(range)}
          className={cn(
            "rounded-md px-2.5 py-1 font-mono text-xs transition-colors",
            range === value ? "bg-background text-foreground shadow-sm" : "text-muted-foreground hover:text-foreground",
          )}
        >
          {range}
        </button>
      ))}
    </div>
  );
}

function bounds(window: MetricsWindow): { from: number; to: number } {
  return { from: new Date(window.from).getTime(), to: new Date(window.to).getTime() };
}

function Loading() {
  return <Loader2Icon className="text-muted-foreground size-5 animate-spin" aria-hidden />;
}

function Failed() {
  const { t } = useTranslation();
  return (
    <p role="alert" className="text-destructive text-sm">
      {t("metrics.errors.load")}
    </p>
  );
}

const grid = "grid gap-6 md:grid-cols-2";

/** One container: CPU against its limit, memory against its limit, network, block I/O, processes. */
export function ContainerMetricsView({ environmentId, container, range }: { environmentId: string; container: string; range: MetricsRange }) {
  const { t } = useTranslation();
  const query = useQuery(containerMetricsQuery(environmentId, container, range));
  if (query.isPending) {
    return <Loading />;
  }
  if (query.isError) {
    return <Failed />;
  }

  const { window, series, deployments } = query.data;
  const { from, to } = bounds(window);
  if (series === null) {
    return <p className="text-muted-foreground text-sm">{t("metrics.noContainerData")}</p>;
  }

  const rows = toRows(series.at, {
    cpu: series.cpu,
    memory: series.memory,
    cache: series.memoryCache,
    rx: series.networkRx,
    tx: series.networkTx,
    read: series.blockRead,
    write: series.blockWrite,
    pids: series.pids,
  });
  const common = { rows, from, to, deployments };
  return (
    <div className={grid}>
      <MetricChart
        {...common}
        title={t("metrics.cpu")}
        series={[{ key: "cpu", label: t("metrics.cpu"), slot: 0 }]}
        format={formatPercent}
        minTop={1}
        limit={last(series.cpuLimit)}
        limitLabel={t("metrics.limit")}
      />
      <MetricChart
        {...common}
        title={t("metrics.memory")}
        series={[{ key: "memory", label: t("metrics.memory"), slot: 0 }]}
        format={formatBytes}
        limit={last(series.memoryLimit)}
      />
      <MetricChart
        {...common}
        title={t("metrics.network")}
        series={[
          { key: "rx", label: t("metrics.received"), slot: 0 },
          { key: "tx", label: t("metrics.sent"), slot: 1 },
        ]}
        format={formatRate}
      />
      <MetricChart
        {...common}
        title={t("metrics.blockIo")}
        series={[
          { key: "read", label: t("metrics.read"), slot: 0 },
          { key: "write", label: t("metrics.written"), slot: 1 },
        ]}
        format={formatRate}
      />
      <MetricChart {...common} title={t("metrics.pids")} series={[{ key: "pids", label: t("metrics.pids"), slot: 0 }]} format={formatCount} integer />
    </div>
  );
}

/** Graphs of a set of containers stacked by name, the busiest seven named and the rest as "Other". */
function StackedContainers({
  series,
  from,
  to,
  deployments,
}: {
  series: readonly ContainerSeries[];
  from: number;
  to: number;
  deployments?: Parameters<typeof MetricChart>[0]["deployments"];
}) {
  const { t } = useTranslation();
  const chart = (title: string, value: (s: ContainerSeries) => readonly Num[], format: (v: number) => string, minTop?: number) => {
    const { rows, keys } = stackRows(series, value);
    const chartSeries: ChartSeries[] = keys.map((k, i) => ({ key: k.key, label: k.name || t("metrics.other"), slot: i }));
    return <MetricChart title={title} rows={rows} series={chartSeries} format={format} from={from} to={to} stacked deployments={deployments} minTop={minTop} />;
  };
  const network = (s: ContainerSeries) => s.networkRx.map((rx, i) => Number(rx) + Number(s.networkTx[i] ?? 0));
  return (
    <div className={grid}>
      {chart(t("metrics.cpu"), (s) => s.cpu, formatPercent, 1)}
      {chart(t("metrics.memory"), (s) => s.memory, formatBytes)}
      {chart(t("metrics.networkTotal"), network, formatRate)}
      {chart(t("metrics.blockWrite"), (s) => s.blockWrite, formatRate)}
    </div>
  );
}

/** The latest value of each container, the table view of the stacked graphs. */
function ContainerTable({ series }: { series: readonly ContainerSeries[] }) {
  const { t } = useTranslation();
  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>{t("metrics.columns.container")}</TableHead>
          <TableHead className="text-right">{t("metrics.cpu")}</TableHead>
          <TableHead className="text-right">{t("metrics.memory")}</TableHead>
          <TableHead className="hidden text-right md:table-cell">{t("metrics.columns.peakMemory")}</TableHead>
          <TableHead className="hidden text-right md:table-cell">{t("metrics.network")}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {series.map((s) => {
          const limit = last(s.memoryLimit);
          return (
            <TableRow key={s.name}>
              <TableCell className="font-mono break-all">{s.name}</TableCell>
              <TableCell className="text-right font-mono tabular-nums">{formatPercent(last(s.cpu))}</TableCell>
              <TableCell className="text-right font-mono tabular-nums">
                {formatBytes(last(s.memory))}
                {limit ? <span className="text-muted-foreground"> / {formatBytes(limit)}</span> : null}
              </TableCell>
              <TableCell className="hidden text-right font-mono tabular-nums md:table-cell">
                {formatBytes(peak(s.memoryMax))}
              </TableCell>
              <TableCell className="hidden text-right font-mono tabular-nums md:table-cell">
                {formatRate((last(s.networkRx) ?? 0) + (last(s.networkTx) ?? 0))}
              </TableCell>
            </TableRow>
          );
        })}
      </TableBody>
    </Table>
  );
}

/** A managed stack: its containers stacked, a table of them, its named volumes. */
export function StackMetricsView({ stackId, range }: { stackId: string; range: MetricsRange }) {
  return <StackMetricsBody query={useQuery(stackMetricsQuery(stackId, range))} />;
}

/** An unmanaged stack (ADR-0016): the same graphs, found by its environment and compose project name. */
export function UnmanagedStackMetricsView({ environmentId, stackName, range }: { environmentId: string; stackName: string; range: MetricsRange }) {
  return <StackMetricsBody query={useQuery(unmanagedStackMetricsQuery(environmentId, stackName, range))} />;
}

function StackMetricsBody({ query }: { query: UseQueryResult<StackMetrics> }) {
  const { t } = useTranslation();
  if (query.isPending) {
    return <Loading />;
  }
  if (query.isError) {
    return <Failed />;
  }

  const { window, containers, volumes, deployments } = query.data;
  const { from, to } = bounds(window);
  if (containers.length === 0) {
    return <p className="text-muted-foreground text-sm">{t("metrics.noStackData")}</p>;
  }

  return (
    <div className="grid gap-6">
      <StackedContainers series={containers} from={from} to={to} deployments={deployments} />
      <ContainerTable series={containers} />
      {volumes.length > 0 ? (
        <div className="grid gap-2">
          <h3 className="text-sm font-medium">{t("metrics.volumes")}</h3>
          <ul className="grid gap-1 text-sm">
            {volumes.map((v) => (
              <li key={v.name} className="flex justify-between gap-4">
                <span className="font-mono break-all">{v.name}</span>
                <span className="font-mono tabular-nums">{formatBytes(v.sizeBytes)}</span>
              </li>
            ))}
          </ul>
        </div>
      ) : null}
    </div>
  );
}

/** An environment: the host (with a role on the whole environment), its disks, Docker's disk usage, sums per stack. */
export function EnvironmentMetricsView({ environmentId, range }: { environmentId: string; range: MetricsRange }) {
  const { t } = useTranslation();
  const query = useQuery(environmentMetricsQuery(environmentId, range));
  const current = useQuery(currentMetricsQuery(environmentId));
  if (query.isPending) {
    return <Loading />;
  }
  if (query.isError) {
    return <Failed />;
  }

  const { window, source, host, stacks, diskUsage } = query.data;
  const { from, to } = bounds(window);
  if (sendsNoMetrics(source)) {
    return <p className="text-muted-foreground text-sm">{t(source === "Disabled" ? "metrics.disabled" : "metrics.unsupported")}</p>;
  }

  const hostRows =
    host === null
      ? []
      : toRows(host.at, {
          user: host.cpuUser,
          system: host.cpuSystem,
          iowait: host.cpuIowait,
          steal: host.cpuSteal,
          memory: host.memoryUsed,
          swap: host.swapUsed,
          rx: host.networkRx,
          tx: host.networkTx,
          read: host.diskRead,
          write: host.diskWrite,
          load: host.load1,
        });
  const filesystems = current.data?.host?.filesystems ?? [];
  const named = stacks.map((s) => ({ ...s, name: s.name || t("metrics.outsideStacks") }));

  return (
    <div className="grid gap-6">
      {host !== null ? (
        <div className={grid}>
          <MetricChart
            title={t("metrics.hostCpu")}
            rows={hostRows}
            from={from}
            to={to}
            stacked
            format={formatPercent}
            minTop={1}
            series={[
              { key: "user", label: t("metrics.cpuUser"), slot: 0 },
              { key: "system", label: t("metrics.cpuSystem"), slot: 1 },
              { key: "iowait", label: t("metrics.cpuIowait"), slot: 2 },
              { key: "steal", label: t("metrics.cpuSteal"), slot: 3 },
            ]}
          />
          <MetricChart
            title={t("metrics.memory")}
            rows={hostRows}
            from={from}
            to={to}
            format={formatBytes}
            limit={last(host.memoryTotal)}
            limitLabel={t("metrics.total")}
            series={[
              { key: "memory", label: t("metrics.used"), slot: 0 },
              { key: "swap", label: t("metrics.swap"), slot: 1 },
            ]}
          />
          <MetricChart
            title={t("metrics.network")}
            rows={hostRows}
            from={from}
            to={to}
            format={formatRate}
            series={[
              { key: "rx", label: t("metrics.received"), slot: 0 },
              { key: "tx", label: t("metrics.sent"), slot: 1 },
            ]}
          />
          <MetricChart
            title={t("metrics.diskIo")}
            rows={hostRows}
            from={from}
            to={to}
            format={formatRate}
            series={[
              { key: "read", label: t("metrics.read"), slot: 0 },
              { key: "write", label: t("metrics.written"), slot: 1 },
            ]}
          />
          <MetricChart
            title={t("metrics.load")}
            rows={hostRows}
            from={from}
            to={to}
            format={(v) => v.toFixed(2)}
            series={[{ key: "load", label: t("metrics.load"), slot: 0 }]}
          />
        </div>
      ) : null}

      {filesystems.length > 0 ? (
        <div className="grid gap-2">
          <h3 className="text-sm font-medium">{t("metrics.disks")}</h3>
          <ul className="grid gap-2">
            {filesystems.map((f) => (
              <li key={f.mountPoint} className="grid gap-1 text-sm">
                <div className="flex justify-between gap-4">
                  <span className="font-mono break-all">{f.mountPoint}</span>
                  <span className="font-mono tabular-nums">
                    {formatBytes(f.usedBytes)} / {formatBytes(f.totalBytes)} · {formatPercent(f.usedRatio)}
                  </span>
                </div>
                <div className="bg-muted h-1.5 overflow-hidden rounded-full">
                  <div
                    className={cn("h-full rounded-full", Number(f.usedRatio) >= 0.9 ? "bg-destructive" : "bg-primary")}
                    style={{ width: `${Math.min(100, Number(f.usedRatio) * 100)}%` }}
                  />
                </div>
              </li>
            ))}
          </ul>
        </div>
      ) : null}

      {diskUsage !== null ? (
        <dl className="grid grid-cols-2 gap-4 text-sm md:grid-cols-4">
          {(
            [
              ["metrics.dockerImages", diskUsage.imagesBytes, diskUsage.imagesReclaimableBytes],
              ["metrics.dockerContainers", diskUsage.containersBytes, null],
              ["metrics.dockerVolumes", diskUsage.volumesBytes, null],
              ["metrics.dockerBuildCache", diskUsage.buildCacheBytes, null],
            ] as const
          ).map(([key, bytes, reclaimable]) => (
            <div key={key} className="grid gap-0.5">
              <dt className="text-muted-foreground">{t(key)}</dt>
              <dd className="font-mono text-base tabular-nums">{formatBytes(bytes)}</dd>
              {reclaimable ? <dd className="text-muted-foreground text-xs">{t("metrics.reclaimable", { size: formatBytes(reclaimable) })}</dd> : null}
            </div>
          ))}
        </dl>
      ) : null}

      {named.length > 0 ? (
        <div className="grid gap-2">
          <h3 className="text-sm font-medium">{t("metrics.byStack")}</h3>
          <StackedContainers series={named} from={from} to={to} />
        </div>
      ) : null}
    </div>
  );
}
