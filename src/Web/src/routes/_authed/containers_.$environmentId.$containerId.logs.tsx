import { useQuery, useSuspenseQuery } from "@tanstack/react-query";
import { Link, createFileRoute } from "@tanstack/react-router";
import { cn } from "cn";
import { type CSSProperties, useLayoutEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { containerLogsQuery, environmentsQuery } from "@/api/queries";
import { ArrowLeftIcon, CopyIcon, DownloadIcon, FilterXIcon, Loader2Icon, RefreshCwIcon, SearchIcon } from "@/components/icons";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import type { MessageKey } from "@/i18n";
import { type AnsiStyle, parseAnsi, stripAnsi } from "@/features/container-logs/ansi";
import {
  type ContainerLogsSearch,
  type LogLine,
  type StreamFilter,
  containerLogsSearchSchema,
  describeLogsError,
  filterLines,
  formatForCopy,
  logFileName,
  streamFilters,
  tailOptions,
  timeOf,
  viewOf,
} from "@/features/container-logs/model";
import type { Environment } from "@/features/containers/model";
import { downloadText } from "@/lib/download";

/** How often auto-refresh asks for newer lines; there is no live log stream (follow) yet. */
const AUTO_REFRESH_MS = 5_000;

/** How close to the bottom (px) still counts as "at the end", so new lines keep the view scrolled down. */
const STICK_THRESHOLD_PX = 24;

export const Route = createFileRoute("/_authed/containers_/$environmentId/$containerId/logs")({
  validateSearch: containerLogsSearchSchema,
  loader: ({ context }) => context.queryClient.ensureQueryData(environmentsQuery),
  component: ContainerLogsScreen,
  errorComponent: ContainerLogsLoadError,
});

/** The end of one container's log: how many lines, which stream, search, auto-refresh, wrapping, colours, copying and saving. */
function ContainerLogsScreen() {
  const { t } = useTranslation();
  const { environmentId, containerId } = Route.useParams();
  const search = Route.useSearch();
  const navigate = Route.useNavigate();
  const { data: environments } = useSuspenseQuery(environmentsQuery);
  const environment = environments.find((e) => e.id === environmentId);

  const setSearch = (patch: Partial<ContainerLogsSearch>) => {
    // Navigating to the same search would only re-run the loader.
    if (Object.entries(patch).every(([key, value]) => search[key as keyof ContainerLogsSearch] === value)) {
      return;
    }

    void navigate({ search: (previous) => ({ ...previous, ...patch }), replace: true });
  };

  return (
    <div className="grid gap-6">
      <div>
        <Button asChild variant="ghost" size="sm" className="-ml-2">
          <Link to="/containers" search={{ environment: environmentId }}>
            <ArrowLeftIcon data-icon="inline-start" />
            {t("containerLogs.back")}
          </Link>
        </Button>
        <h1 className="font-heading mt-2 text-2xl font-semibold break-all">{t("containerLogs.title", { name: containerId })}</h1>
        {environment && <p className="text-muted-foreground mt-1">{t("containerLogs.description", { environment: environment.name })}</p>}
      </div>

      {environment === undefined ? (
        <p role="alert" className="text-destructive">
          {t("containerLogs.errors.environmentNotFound")}
        </p>
      ) : !environment.isOnline ? (
        <p role="status" className="text-muted-foreground">
          {t("containerLogs.agentOffline", { name: environment.name })}
        </p>
      ) : (
        <ContainerLog environment={environment} container={containerId} search={search} setSearch={setSearch} />
      )}
    </div>
  );
}

/** The result of the last copy. Kept as a key, not text, so it follows a language switch. */
interface Notice {
  kind: "success" | "error";
  key: MessageKey;
  count?: number;
}

function ContainerLog({
  environment,
  container,
  search,
  setSearch,
}: {
  environment: Environment;
  container: string;
  search: ContainerLogsSearch;
  setSearch: (patch: Partial<ContainerLogsSearch>) => void;
}) {
  const { t, i18n } = useTranslation();
  const view = viewOf(search);
  const logs = useQuery({
    ...containerLogsQuery(environment.id, container, view.tail),
    refetchInterval: view.auto ? AUTO_REFRESH_MS : false,
  });
  const [notice, setNotice] = useState<Notice>();

  const lines = logs.data ?? [];
  const shown = filterLines(lines, view.stream, view.q);
  const filtered = view.stream !== "all" || view.q !== "";

  // New lines keep the view at the end only when the user has not scrolled up to read something.
  const scroller = useRef<HTMLDivElement>(null);
  const stickToEnd = useRef(true);
  useLayoutEffect(() => {
    const element = scroller.current;
    if (element && stickToEnd.current) {
      element.scrollTop = element.scrollHeight;
    }
  }, [shown.length, logs.dataUpdatedAt]);

  const errorText = (error: unknown) => {
    const { key, detail } = describeLogsError(error);
    return detail ? `${t(key)} ${detail}` : t(key);
  };

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(formatForCopy(shown, view.time));
      setNotice({ kind: "success", key: "containerLogs.copied", count: shown.length });
    } catch {
      // No clipboard access: an insecure origin or a denied permission.
      setNotice({ kind: "error", key: "containerLogs.copyFailed" });
    }
  };

  const download = () => {
    downloadText(formatForCopy(shown, view.time), logFileName(container, new Date()));
    setNotice({ kind: "success", key: "containerLogs.downloaded", count: shown.length });
  };

  const timeFormat = new Intl.DateTimeFormat(i18n.resolvedLanguage, {
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    fractionalSecondDigits: 3,
  });

  return (
    <div className="grid gap-4">
      <div className="flex flex-wrap items-end gap-3">
        <div className="grid gap-1.5">
          <Label htmlFor="log-tail">{t("containerLogs.tail")}</Label>
          <Select
            value={String(view.tail)}
            // Radix Select also reports its current value, or "" while it matches no item; neither is a choice.
            onValueChange={(value) => value !== "" && setSearch({ tail: Number(value) })}
          >
            <SelectTrigger id="log-tail" className="min-w-28">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {tailOptions.map((tail) => (
                <SelectItem key={tail} value={String(tail)}>
                  {t("containerLogs.tailOption", { lines: tail })}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>

        <div className="grid gap-1.5">
          <Label htmlFor="log-stream">{t("containerLogs.stream")}</Label>
          <Select
            value={view.stream}
            onValueChange={(value) =>
              value !== "" && setSearch({ stream: value === "all" ? undefined : (value as StreamFilter) })
            }
          >
            <SelectTrigger id="log-stream" className="min-w-32">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {streamFilters.map((stream) => (
                <SelectItem key={stream} value={stream}>
                  {t(`containerLogs.streams.${stream}`)}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>

        <div className="grid min-w-56 flex-1 gap-1.5">
          <Label htmlFor="log-search">{t("containerLogs.search")}</Label>
          <div className="relative">
            <SearchIcon className="text-muted-foreground pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2" />
            <Input
              id="log-search"
              type="search"
              className="pl-8"
              value={view.q}
              onChange={(event) => setSearch({ q: event.target.value === "" ? undefined : event.target.value })}
            />
          </div>
        </div>

        <Button variant="ghost" disabled={!filtered} onClick={() => setSearch({ stream: undefined, q: undefined })}>
          <FilterXIcon data-icon="inline-start" />
          {t("containerLogs.reset")}
        </Button>

        <Button variant="outline" disabled={logs.isFetching} onClick={() => void logs.refetch()}>
          {logs.isFetching ? <Loader2Icon data-icon="inline-start" className="animate-spin" /> : <RefreshCwIcon data-icon="inline-start" />}
          {t("containerLogs.refresh")}
        </Button>

        <Button variant="outline" disabled={shown.length === 0} onClick={() => void copy()}>
          <CopyIcon data-icon="inline-start" />
          {t("containerLogs.copy")}
        </Button>

        <Button variant="outline" disabled={shown.length === 0} onClick={download}>
          <DownloadIcon data-icon="inline-start" />
          {t("containerLogs.download")}
        </Button>
      </div>

      <div className="flex flex-wrap gap-x-6 gap-y-2">
        <Option
          id="log-auto"
          label={t("containerLogs.autoRefresh", { seconds: AUTO_REFRESH_MS / 1000 })}
          checked={view.auto}
          onChange={(checked) => setSearch({ auto: checked ? true : undefined })}
        />
        <Option
          id="log-wrap"
          label={t("containerLogs.wrap")}
          checked={view.wrap}
          onChange={(checked) => setSearch({ wrap: checked ? undefined : false })}
        />
        <Option
          id="log-time"
          label={t("containerLogs.showTime")}
          checked={view.time}
          onChange={(checked) => setSearch({ time: checked ? undefined : false })}
        />
        <Option
          id="log-color"
          label={t("containerLogs.showColors")}
          checked={view.color}
          onChange={(checked) => setSearch({ color: checked ? undefined : false })}
        />
      </div>

      <div aria-live="polite">
        {notice && (
          <p role={notice.kind === "error" ? "alert" : "status"} className={notice.kind === "error" ? "text-destructive text-sm" : "text-sm"}>
            {t(notice.key, { lines: notice.count })}
          </p>
        )}
        {logs.isError && lines.length > 0 && (
          <p role="alert" className="text-destructive text-sm">
            {t("containerLogs.errors.refreshFailed")} {errorText(logs.error)}
          </p>
        )}
      </div>

      {logs.isPending ? (
        <p className="text-muted-foreground flex items-center gap-2">
          <Loader2Icon className="animate-spin" />
          {t("containerLogs.loading")}
        </p>
      ) : logs.isError && lines.length === 0 ? (
        <p role="alert" className="text-destructive">
          {t("containerLogs.errors.loadFailed")} {errorText(logs.error)}
        </p>
      ) : lines.length === 0 ? (
        <p className="text-muted-foreground">{t("containerLogs.empty")}</p>
      ) : (
        <>
          <div
            ref={scroller}
            role="log"
            aria-label={t("containerLogs.logLabel")}
            tabIndex={0}
            className="bg-muted/40 max-h-[65vh] overflow-auto rounded-lg border py-2 font-mono text-xs"
            onScroll={(event) => {
              const element = event.currentTarget;
              stickToEnd.current = element.scrollHeight - element.scrollTop - element.clientHeight < STICK_THRESHOLD_PX;
            }}
          >
            {shown.length === 0 ? (
              <p className="text-muted-foreground px-3 font-sans text-sm">{t("containerLogs.noMatches")}</p>
            ) : (
              shown.map((line, index) => (
                // Lines have no id; the list only grows at the end, so the position is stable.
                <LogRow key={index} line={line} wrap={view.wrap} color={view.color} time={view.time ? formatTime(line, timeFormat) : undefined} />
              ))
            )}
          </div>
          <p className="text-muted-foreground text-sm">
            {t("containerLogs.count", { shown: shown.length, total: lines.length })}
            {logs.dataUpdatedAt > 0 && (
              <>
                {" · "}
                {t("containerLogs.updatedAt", {
                  time: new Intl.DateTimeFormat(i18n.resolvedLanguage, { timeStyle: "medium" }).format(logs.dataUpdatedAt),
                })}
              </>
            )}
          </p>
        </>
      )}
    </div>
  );
}

function formatTime(line: LogLine, format: Intl.DateTimeFormat): string {
  const ms = timeOf(line);
  return ms === null ? "—" : format.format(ms);
}

function LogRow({ line, wrap, color, time }: { line: LogLine; wrap: boolean; color: boolean; time: string | undefined }) {
  const { t } = useTranslation();
  const stderr = line.stream === "Stderr";

  return (
    <div
      className={cn(
        "flex gap-3 border-l-2 px-3 py-px",
        stderr ? "border-destructive bg-destructive/5" : "border-transparent",
        !wrap && "w-max min-w-full",
      )}
    >
      {time !== undefined && <span className="text-muted-foreground shrink-0 select-none">{time}</span>}
      {stderr && <span className="sr-only">{t("containerLogs.streams.stderr")}: </span>}
      <span className={wrap ? "break-all whitespace-pre-wrap" : "whitespace-pre"}>
        {color
          ? parseAnsi(line.text).map((segment, index) =>
              Object.keys(segment.style).length === 0 ? (
                segment.text
              ) : (
                // Segments of a line never move, so the position is a stable key.
                <span key={index} style={cssOf(segment.style)}>
                  {segment.text}
                </span>
              ),
            )
          : stripAnsi(line.text)}
      </span>
    </div>
  );
}

/**
 * Inline style for a run of coloured text. React applies it through the CSSOM, which the panel's CSP allows.
 * Inverse swaps the colours, falling back to the log's own text and background colours.
 */
function cssOf(style: AnsiStyle): CSSProperties {
  const color = style.inverse ? (style.background ?? "var(--background)") : style.color;
  const background = style.inverse ? (style.color ?? "var(--foreground)") : style.background;
  const decorations = [style.underline && "underline", style.strike && "line-through"].filter(Boolean);

  return {
    color,
    backgroundColor: background,
    fontWeight: style.bold ? 700 : undefined,
    opacity: style.dim ? 0.7 : undefined,
    fontStyle: style.italic ? "italic" : undefined,
    textDecorationLine: decorations.length > 0 ? decorations.join(" ") : undefined,
  };
}

function Option({
  id,
  label,
  checked,
  onChange,
}: {
  id: string;
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
}) {
  return (
    <div className="flex items-center gap-2">
      <Checkbox id={id} checked={checked} onCheckedChange={(state) => onChange(state === true)} />
      <Label htmlFor={id} className="font-normal">
        {label}
      </Label>
    </div>
  );
}

function ContainerLogsLoadError() {
  const { t } = useTranslation();

  return (
    <p role="alert" className="text-destructive">
      {t("containers.errors.loadEnvironmentsFailed")}
    </p>
  );
}
