import { z } from "zod";
import { ApiError } from "@/api/client";
import type { components } from "@/api/schema.gen";
import { describeError } from "@/features/containers/model";
import type { MessageKey } from "@/i18n";

// What the container logs screen shows, without React: URL state, joining newer lines, filters, copy text, errors.

export type LogLine = components["schemas"]["ContainerLogLineResponse"];
export type LogStream = components["schemas"]["LogStream"];

/** Choices for how many of the last lines to load; the aggregator accepts 1..5000. */
export const tailOptions = [100, 200, 500, 1000, 5000] as const;
export const DEFAULT_TAIL = 200;

/** The aggregator returns at most this many lines, and the screen keeps no more than this while it loads newer ones. */
export const MAX_LINES = 5000;

export const streamFilters = ["all", "stdout", "stderr"] as const;
export type StreamFilter = (typeof streamFilters)[number];

/** The screen's URL: what to load and how to show it, so a link opens the same view. */
export const containerLogsSearchSchema = z.object({
  tail: z.coerce
    .number()
    .refine((n) => (tailOptions as readonly number[]).includes(n))
    .optional()
    .catch(undefined),
  stream: z.enum(streamFilters).optional().catch(undefined),
  q: z.string().optional().catch(undefined),
  auto: z.boolean().optional().catch(undefined),
  wrap: z.boolean().optional().catch(undefined),
  time: z.boolean().optional().catch(undefined),
});

export type ContainerLogsSearch = z.infer<typeof containerLogsSearchSchema>;

/** Values the URL leaves out: the default tail, all streams, wrapped lines with their time, no auto-refresh. */
export function viewOf(search: ContainerLogsSearch) {
  return {
    tail: search.tail ?? DEFAULT_TAIL,
    stream: search.stream ?? "all",
    q: search.q ?? "",
    auto: search.auto ?? false,
    wrap: search.wrap ?? true,
    time: search.time ?? true,
  };
}

/** Milliseconds since the epoch of a line, or null when the agent did not report its time. */
export function timeOf(line: LogLine): number | null {
  if (line.at === null) {
    return null;
  }

  const ms = Date.parse(line.at);
  return Number.isNaN(ms) ? null : ms;
}

/**
 * Where to ask for newer lines: the latest time among the loaded lines. Stdout and stderr lines are not strictly
 * in time order, so it is the maximum, not the last line's. Null when nothing is loaded or no line has a time;
 * then the whole tail is loaded again.
 */
export function nextSince(lines: readonly LogLine[]): string | null {
  let latest: number | null = null;
  for (const line of lines) {
    const ms = timeOf(line);
    if (ms !== null && (latest === null || ms > latest)) {
      latest = ms;
    }
  }

  return latest === null ? null : new Date(latest).toISOString();
}

const lineKey = (line: LogLine) => `${line.stream}\u0000${line.text}`;

/**
 * Appends lines loaded with since = {@link nextSince} to the loaded ones. The bound is inclusive, so lines at
 * exactly that millisecond come again; each one is dropped once for every equal line already loaded at that time,
 * which keeps a line a program really printed twice. At most {@link MAX_LINES} of the newest lines are kept.
 */
export function mergeLines(loaded: readonly LogLine[], newer: readonly LogLine[], since: string): LogLine[] {
  const boundary = Date.parse(since);
  const seen = new Map<string, number>();
  for (const line of loaded) {
    if (timeOf(line) === boundary) {
      seen.set(lineKey(line), (seen.get(lineKey(line)) ?? 0) + 1);
    }
  }

  const added = newer.filter((line) => {
    if (timeOf(line) !== boundary) {
      return true;
    }

    const count = seen.get(lineKey(line)) ?? 0;
    if (count === 0) {
      return true;
    }

    seen.set(lineKey(line), count - 1);
    return false;
  });

  const merged = [...loaded, ...added];
  return merged.length > MAX_LINES ? merged.slice(merged.length - MAX_LINES) : merged;
}

/** The lines the filters let through: one stream or both, and text containing the search, ignoring case. */
export function filterLines(lines: readonly LogLine[], stream: StreamFilter, query: string): LogLine[] {
  const text = query.trim().toLowerCase();
  return lines.filter(
    (line) =>
      (stream === "all" || line.stream.toLowerCase() === stream) &&
      (text === "" || line.text.toLowerCase().includes(text)),
  );
}

/** Lines as plain text for the clipboard, one per line, with the ISO time and stream when asked for. */
export function formatForCopy(lines: readonly LogLine[], withTime: boolean): string {
  return lines
    .map((line) => {
      if (!withTime) {
        return line.text;
      }

      const ms = timeOf(line);
      const time = ms === null ? "-" : new Date(ms).toISOString();
      return `${time} ${line.stream === "Stderr" ? "stderr" : "stdout"} ${line.text}`;
    })
    .join("\n");
}

/**
 * Whether a failed load is worth repeating. The aggregator's answer for a missing container, an old or offline
 * agent and a denied request will not change on a second try, while the user waits on "Loading"; a lost
 * connection or an agent error may.
 */
export function shouldRetryLoad(failureCount: number, error: unknown): boolean {
  if (error instanceof ApiError && (error.status < 500 || error.status === 501 || error.status === 503)) {
    return false;
  }

  return failureCount < 2;
}

/**
 * A translated explanation for a failed load. 404 and 501 mean something specific here (the container is gone;
 * the agent is older than log reading), the rest are explained as on the containers screen.
 */
export function describeLogsError(error: unknown): { key: MessageKey; detail?: string } {
  if (error instanceof ApiError) {
    if (error.status === 404) {
      return { key: "containerLogs.errors.notFound" };
    }

    if (error.status === 501) {
      return { key: "containerLogs.errors.agentOutdated" };
    }

    if (error.status === 503) {
      return { key: "containerLogs.errors.agentOffline" };
    }
  }

  return describeError(error);
}
