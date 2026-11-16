import { useTranslation } from "react-i18next";
import { Area, AreaChart, CartesianGrid, Line, LineChart, ReferenceLine, XAxis, YAxis } from "recharts";
import { type ChartConfig, ChartContainer, ChartTooltip } from "@/components/ui/chart";
import {
  type ChartRow,
  type DeploymentMarker,
  formatTooltipTime,
  seriesColors,
  timeTickFormatter,
} from "@/features/metrics/model";

export interface ChartSeries {
  /** The row key of the series. */
  key: string;
  label: string;
  /** The categorical slot; fixed per series, never its rank. */
  slot: number;
}

interface MetricChartProps {
  title: string;
  rows: ChartRow[];
  series: ChartSeries[];
  format: (value: number) => string;
  /** The window shown, in Unix milliseconds: the axis spans it even where there are no points. */
  from: number;
  to: number;
  /** Stacked areas (parts of a whole) instead of lines. */
  stacked?: boolean;
  /** A horizontal reference line, e.g. the memory limit; nothing for null or 0. */
  limit?: number | null;
  limitLabel?: string;
  deployments?: readonly DeploymentMarker[];
}

/**
 * One measure over time on one axis. Lines are 2px, a single series gets a 10 % wash, stacked series areas; the grid
 * is horizontal hairlines only. A crosshair tooltip lists every series at the pointer, and a legend with the latest
 * values sits under the plot for two or more series, so identity and values never depend on colour alone.
 */
export function MetricChart({ title, rows, series, format, from, to, stacked = false, limit, limitLabel, deployments = [] }: MetricChartProps) {
  const { t, i18n } = useTranslation();
  const config: ChartConfig = Object.fromEntries(
    series.map((s) => {
      const color = seriesColors[s.slot % seriesColors.length]!;
      return [s.key, { label: s.label, theme: { light: color.light, dark: color.dark } }];
    }),
  );
  const tick = timeTickFormatter(to - from, i18n.language);
  const latest = rows.at(-1);
  const single = series.length === 1;

  const plot = (
    <>
      <CartesianGrid vertical={false} />
      <XAxis
        dataKey="at"
        type="number"
        scale="time"
        domain={[from, to]}
        tickFormatter={tick}
        tickLine={false}
        axisLine={false}
        minTickGap={48}
      />
      <YAxis tickFormatter={format} tickLine={false} axisLine={false} width={72} allowDecimals />
      <ChartTooltip
        cursor={{ strokeWidth: 1 }}
        content={({ active, payload, label }) =>
          active && payload?.length ? (
            <div className="bg-background border-border/50 grid min-w-40 gap-1.5 rounded-lg border px-2.5 py-1.5 text-xs shadow-xl">
              <div className="text-muted-foreground">{formatTooltipTime(Number(label), i18n.language)}</div>
              {[...payload].reverse().map((item) => (
                <div key={String(item.dataKey)} className="flex items-center gap-2">
                  <span className="h-0.5 w-3 shrink-0 rounded-full" style={{ backgroundColor: `var(--color-${String(item.dataKey)})` }} />
                  <span className="text-foreground font-mono font-medium tabular-nums">{format(Number(item.value))}</span>
                  <span className="text-muted-foreground truncate">{config[String(item.dataKey)]?.label}</span>
                </div>
              ))}
            </div>
          ) : null
        }
      />
      {limit ? (
        <ReferenceLine
          y={limit}
          stroke="var(--muted-foreground)"
          strokeWidth={1}
          ifOverflow="extendDomain"
          label={{ value: limitLabel ?? t("metrics.limit"), position: "insideTopRight", fill: "var(--muted-foreground)", fontSize: 11 }}
        />
      ) : null}
      {deployments.map((d) => (
        <ReferenceLine
          key={d.id}
          x={new Date(d.at).getTime()}
          stroke={d.status === "Failed" ? "var(--destructive)" : "var(--muted-foreground)"}
          strokeWidth={1}
          strokeOpacity={0.6}
          label={{ value: "▲", position: "insideBottom", fill: "var(--muted-foreground)", fontSize: 9 }}
        />
      ))}
    </>
  );

  return (
    <figure className="grid gap-2">
      <figcaption className="flex items-baseline justify-between gap-2">
        <span className="text-sm font-medium">{title}</span>
        {single && latest ? (
          <span className="text-muted-foreground font-mono text-xs tabular-nums">{format(latest[series[0]!.key] ?? 0)}</span>
        ) : null}
      </figcaption>
      {rows.length === 0 ? (
        <p className="text-muted-foreground flex h-44 items-center justify-center text-sm">{t("metrics.noData")}</p>
      ) : (
        <ChartContainer config={config} className="aspect-auto h-44 w-full">
          {stacked || single ? (
            <AreaChart data={rows} margin={{ top: 8, right: 8, left: 0, bottom: 0 }}>
              {plot}
              {series.map((s) => (
                <Area
                  key={s.key}
                  dataKey={s.key}
                  type="monotone"
                  stackId={stacked ? "a" : undefined}
                  stroke={`var(--color-${s.key})`}
                  strokeWidth={stacked ? 1 : 2}
                  fill={`var(--color-${s.key})`}
                  fillOpacity={stacked ? 0.35 : 0.1}
                  isAnimationActive={false}
                  dot={false}
                />
              ))}
            </AreaChart>
          ) : (
            <LineChart data={rows} margin={{ top: 8, right: 8, left: 0, bottom: 0 }}>
              {plot}
              {series.map((s) => (
                <Line
                  key={s.key}
                  dataKey={s.key}
                  type="monotone"
                  stroke={`var(--color-${s.key})`}
                  strokeWidth={2}
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  isAnimationActive={false}
                  dot={false}
                />
              ))}
            </LineChart>
          )}
        </ChartContainer>
      )}
      {!single && series.length > 0 ? (
        <ul className="flex flex-wrap gap-x-4 gap-y-1 text-xs">
          {series.map((s) => (
            <li key={s.key} className="flex items-center gap-1.5">
              <span
                className={stacked ? "size-2.5 rounded-sm" : "h-0.5 w-3 rounded-full"}
                style={{ backgroundColor: seriesColorVar(s.slot) }}
                data-slot={s.slot}
              />
              <span className="text-muted-foreground">{s.label}</span>
              {latest ? <span className="font-mono tabular-nums">{format(latest[s.key] ?? 0)}</span> : null}
            </li>
          ))}
        </ul>
      ) : null}
    </figure>
  );
}

/** The slot's colour outside a chart (legend swatches), following the theme like the chart's own variables. */
function seriesColorVar(slot: number): string {
  return `var(--series-${(slot % seriesColors.length) + 1})`;
}
