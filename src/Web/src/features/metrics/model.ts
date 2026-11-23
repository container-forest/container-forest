import type { components } from "@/api/schema.gen";
import type { MetricsRange } from "@/api/queries";

export type ContainerSeries = components["schemas"]["ContainerSeriesResponse"];
export type HostSeries = components["schemas"]["HostSeriesResponse"];
export type FilesystemSeries = components["schemas"]["FilesystemSeriesResponse"];
export type MetricsWindow = components["schemas"]["MetricsWindowResponse"];
export type DeploymentMarker = components["schemas"]["DeploymentMarkerResponse"];
export type CurrentMetrics = components["schemas"]["CurrentMetricsResponse"];
export type StackMetrics = components["schemas"]["StackMetricsResponse"];
export type MetricsSource = components["schemas"]["MetricsSourceKind"];

export const metricsRanges = ["15m", "1h", "6h", "24h", "7d", "30d", "90d"] as const satisfies readonly MetricsRange[];

export function isMetricsRange(value: unknown): value is MetricsRange {
  return typeof value === "string" && (metricsRanges as readonly string[]).includes(value);
}

/**
 * The categorical palette of the graphs: the dataviz reference palette, validated against the panel's card surfaces
 * (light #fdfffd, dark #0f1a15) for colour-vision deficiency and contrast. Slots go in this order and are never
 * cycled; past eight series the rest folds into "Other". Three light slots are below 3:1 against the surface, so every
 * multi-series graph has a legend with the latest values.
 */
export const seriesColors: readonly { light: string; dark: string }[] = [
  { light: "#2a78d6", dark: "#3987e5" },
  { light: "#eb6834", dark: "#d95926" },
  { light: "#1baf7a", dark: "#199e70" },
  { light: "#eda100", dark: "#c98500" },
  { light: "#e87ba4", dark: "#d55181" },
  { light: "#008300", dark: "#008300" },
  { light: "#6250d6", dark: "#9085e9" },
  { light: "#e34948", dark: "#e66767" },
];

/** Series a stacked graph shows by name; the rest are summed into one "Other" band. */
export const MAX_SERIES = seriesColors.length - 1;

/** The OpenAPI document lets numbers arrive as strings; every value is read through Number. */
export type Num = number | string;

/** One point of a graph: the time in Unix milliseconds and a value per series key. */
export type ChartRow = { at: number } & Record<string, number>;

/** Turns the API's columns into rows: rows[i] = { at: at[i], key: values[key][i], ... }. */
export function toRows(at: readonly Num[], columns: Record<string, readonly Num[]>): ChartRow[] {
  return at.map((time, i) => {
    const row: ChartRow = { at: Number(time) } as ChartRow;
    for (const [key, values] of Object.entries(columns)) {
      row[key] = Number(values[i] ?? 0);
    }
    return row;
  });
}

/**
 * Rows of a stacked graph: one key per series (s0, s1, …), aligned on the union of their times, a missing point
 * counted as 0 (the container was not running). Series beyond <see cref="MAX_SERIES"/>, smallest by average first,
 * are summed into "other".
 */
export function stackRows<T extends { name: string; at: readonly Num[] }>(
  series: readonly T[],
  value: (s: T) => readonly Num[],
): { rows: ChartRow[]; keys: { key: string; name: string | null }[] } {
  const average = (s: T) => value(s).reduce<number>((sum, v) => sum + Number(v), 0) / Math.max(1, value(s).length);
  const ranked = [...series].sort((a, b) => average(b) - average(a));
  const named = ranked.length > MAX_SERIES + 1 ? ranked.slice(0, MAX_SERIES) : ranked;
  const folded = ranked.slice(named.length);
  // Keys follow the series' own order (by name), not their rank, so a colour stays with its container.
  const ordered = [...named].sort((a, b) => a.name.localeCompare(b.name));
  const keys: { key: string; name: string | null }[] = ordered.map((s, i) => ({ key: `s${i}`, name: s.name }));
  if (folded.length > 0) {
    keys.push({ key: "other", name: null });
  }

  const rows = new Map<number, ChartRow>();
  const add = (time: number, key: string, v: number) => {
    const row = rows.get(time) ?? ({ at: time } as ChartRow);
    row[key] = (row[key] ?? 0) + v;
    rows.set(time, row);
  };
  ordered.forEach((s, i) => s.at.forEach((time, j) => add(Number(time), `s${i}`, Number(value(s)[j] ?? 0))));
  folded.forEach((s) => s.at.forEach((time, j) => add(Number(time), "other", Number(value(s)[j] ?? 0))));

  const sorted = [...rows.values()].sort((a, b) => a.at - b.at);
  for (const row of sorted) {
    for (const { key } of keys) {
      row[key] ??= 0;
    }
  }
  return { rows: sorted, keys };
}

/** The last value of a series, for legends and tables; null for an empty one. */
export function last(values: readonly Num[]): number | null {
  return values.length > 0 ? Number(values[values.length - 1]) : null;
}

/** The highest value of a series; 0 for an empty one. */
export function peak(values: readonly Num[]): number {
  return values.reduce<number>((max, v) => Math.max(max, Number(v)), 0);
}

/** Binary units, as docker stats shows memory: 1.5 GiB, 300 MiB. */
export function formatBytes(bytes: Num | null | undefined): string {
  if (bytes === null || bytes === undefined || !Number.isFinite(Number(bytes))) {
    return "–";
  }
  const units = ["B", "KiB", "MiB", "GiB", "TiB"];
  let value = Math.max(0, Number(bytes));
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(unit === 0 || value >= 100 ? 0 : 1)} ${units[unit]}`;
}

/**
 * Value-axis ticks for bytes (or bytes per second) at round binary steps: 1, 2 or 5 times a power of ten of the unit
 * `formatBytes` will show (B, KiB, MiB…), from 0 to the first step at or above `max`, or in quarters up to 1024 of the
 * unit when that would pass 999 of it. Recharts' own ticks are round in decimal, so they read as 47.7 MiB or 5.4 KiB/s.
 * Undefined for no positive value, which keeps Recharts' empty scale.
 */
export function binaryTicks(max: number, count = 4): number[] | undefined {
  if (!(max > 0) || !Number.isFinite(max)) {
    return undefined;
  }
  let unit = 1;
  while (max / unit >= 1024 && unit < 1024 ** 4) {
    unit *= 1024;
  }
  const raw = max / unit / count;
  const magnitude = 10 ** Math.floor(Math.log10(raw));
  // Below a kibibyte a step is at least 1 B: formatBytes shows whole bytes.
  let step = Math.max(unit === 1 ? 1 : 0, [1, 2, 5, 10].map((m) => m * magnitude).find((s) => s >= raw)!) * unit;
  let top = Math.ceil(max / step - 1e-9) * step;
  // Steps past 999 of a unit read as "1000 KiB/s", too wide for the axis: go up to 1024 of it (1 MiB) in quarters.
  if (top / unit > 999 && max / unit <= 1024) {
    step = 256 * unit;
    top = 1024 * unit;
  }
  return Array.from({ length: Math.round(top / step) + 1 }, (_, i) => i * step);
}

export function formatRate(bytesPerSecond: number | null | undefined): string {
  return bytesPerSecond === null || bytesPerSecond === undefined ? "–" : `${formatBytes(bytesPerSecond)}/s`;
}

/** A share (0..1, or cores for containers: 1.5 = 150 %) as a percentage. */
export function formatPercent(ratio: Num | null | undefined): string {
  if (ratio === null || ratio === undefined || !Number.isFinite(Number(ratio))) {
    return "–";
  }
  const percent = Number(ratio) * 100;
  return `${percent.toFixed(percent < 10 ? 1 : 0)}%`;
}

export function formatCount(value: number | null | undefined): string {
  return value === null || value === undefined ? "–" : Math.round(value).toString();
}

/** Axis ticks of time: hours and minutes within a day, the date beyond it. */
export function timeTickFormatter(rangeMs: number, language: string | undefined): (at: number) => string {
  const format = new Intl.DateTimeFormat(
    language,
    rangeMs > 36 * 3_600_000 ? { day: "numeric", month: "short" } : { hour: "2-digit", minute: "2-digit" },
  );
  return (at) => format.format(at);
}

export function formatTooltipTime(at: number, language: string | undefined): string {
  return new Intl.DateTimeFormat(language, { dateStyle: "medium", timeStyle: "short" }).format(at);
}

/** Whether a source means the agent sends no usage at all. */
export function sendsNoMetrics(source: MetricsSource | undefined): boolean {
  return source === "Unsupported" || source === "Disabled";
}
