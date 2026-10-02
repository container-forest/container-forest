import { useQuery, useSuspenseQuery } from "@tanstack/react-query";
import { createFileRoute, redirect } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useContainerActionMutation } from "@/api/containers";
import { containersQuery, environmentsQuery } from "@/api/queries";
import {
  FilterXIcon,
  Loader2Icon,
  PlayIcon,
  RefreshCwIcon,
  RotateCwIcon,
  SearchIcon,
  SquareIcon,
} from "@/components/icons";
import {
  AlertDialog,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import type { MessageKey } from "@/i18n";
import {
  ALL_STACKS,
  type Container,
  type ContainerAction,
  type ContainersSearch,
  DEFAULT_STOP_TIMEOUT,
  type Environment,
  MAX_STOP_TIMEOUT,
  NO_STACK,
  type StateFilter,
  containersSearchSchema,
  describeError,
  filterContainers,
  isActionAvailable,
  needsConfirmation,
  parseStopTimeout,
  stackOptions,
  stateFilterOf,
  stateFilters,
} from "@/features/containers/model";

/** Without SignalR the list is reloaded this often, so the states do not go stale while the screen is open. */
const REFRESH_INTERVAL_MS = 15_000;

export const Route = createFileRoute("/_authed/containers")({
  validateSearch: containersSearchSchema,
  beforeLoad: async ({ context, search }) => {
    const environments = await context.queryClient.ensureQueryData(environmentsQuery);
    // No environment in the URL, or one the user cannot see (any more): open the first one, keeping the filters.
    const first = environments[0];
    if (first !== undefined && !environments.some((e) => e.id === search.environment)) {
      throw redirect({ to: "/containers", search: { ...search, environment: first.id }, replace: true });
    }
  },
  component: ContainersScreen,
  errorComponent: ContainersLoadError,
});

/** The containers of one environment: filters in the URL, start/stop/restart with confirmation and stop timeout. */
function ContainersScreen() {
  const { t } = useTranslation();
  const search = Route.useSearch();
  const navigate = Route.useNavigate();
  const { data: environments } = useSuspenseQuery(environmentsQuery);
  const environment = environments.find((e) => e.id === search.environment);

  const setSearch = (patch: Partial<ContainersSearch>) => {
    // Navigating to the same search would only re-run the loader.
    if (Object.entries(patch).every(([key, value]) => search[key as keyof ContainersSearch] === value)) {
      return;
    }

    void navigate({ search: (previous) => ({ ...previous, ...patch }), replace: true });
  };

  return (
    <div className="grid gap-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="font-heading text-2xl font-semibold">{t("containers.title")}</h1>
          <p className="text-muted-foreground mt-1">{t("containers.description")}</p>
        </div>
        {environments.length > 0 && (
          <div className="grid gap-1.5">
            <Label htmlFor="environment">{t("containers.environment")}</Label>
            <Select
              value={search.environment}
              // Stacks differ between environments, so the stack filter does not carry over.
              // Radix Select also reports its current value, or "" while the value matches no item, without the
              // user choosing anything; only a different environment resets the stack filter.
              onValueChange={(id) => id !== "" && id !== search.environment && setSearch({ environment: id, stack: undefined })}
            >
              <SelectTrigger id="environment" className="min-w-56">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {environments.map((e) => (
                  <SelectItem key={e.id} value={e.id}>
                    {e.name}
                    {!e.isOnline && <span className="text-muted-foreground">{t("containers.offlineMark")}</span>}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        )}
      </div>

      {environment === undefined ? (
        <p className="text-muted-foreground">{t("containers.noEnvironments")}</p>
      ) : (
        // A new key per environment drops the previous one's dialog and messages.
        <EnvironmentContainers key={environment.id} environment={environment} search={search} setSearch={setSearch} />
      )}
    </div>
  );
}

/** The outcome of the last action. Kept as a key, not text, so it follows a language switch. */
interface Notice {
  kind: "success" | "error";
  key: MessageKey;
  name?: string;
  detail?: string;
}

interface PendingAction {
  container: Container;
  action: "stop" | "restart";
}

function EnvironmentContainers({
  environment,
  search,
  setSearch,
}: {
  environment: Environment;
  search: ContainersSearch;
  setSearch: (patch: Partial<ContainersSearch>) => void;
}) {
  const { t, i18n } = useTranslation();
  const containers = useQuery({
    ...containersQuery(environment.id),
    // An offline agent answers 503 every time; there is nothing to load until it reconnects.
    enabled: environment.isOnline,
    refetchInterval: REFRESH_INTERVAL_MS,
  });
  const action = useContainerActionMutation(environment.id);
  const [pending, setPending] = useState<PendingAction>();
  const [notice, setNotice] = useState<Notice>();

  const errorText = (error: unknown) => {
    const { key, detail } = describeError(error);
    return detail ? `${t(key)} ${detail}` : t(key);
  };

  const noticeText = ({ key, name, detail }: Notice) => {
    const text = t(key, { name });
    return detail ? `${text} ${detail}` : text;
  };

  const run = (container: Container, kind: ContainerAction, timeoutSeconds?: number) => {
    setNotice(undefined);
    action.mutate(
      { containerId: container.id, action: kind, timeoutSeconds },
      {
        onSuccess: () => setNotice({ kind: "success", key: `containers.done.${kind}`, name: container.name }),
        onError: (error) => setNotice({ kind: "error", ...describeError(error) }),
      },
    );
  };

  const onAction = (container: Container, kind: ContainerAction) => {
    if (needsConfirmation(kind)) {
      setPending({ container, action: kind });
    } else {
      run(container, kind);
    }
  };

  if (!environment.isOnline) {
    return (
      <p role="status" className="text-muted-foreground">
        {t("containers.agentOffline", { name: environment.name })}
      </p>
    );
  }

  const all = containers.data ?? [];
  const shown = filterContainers(all, search);
  const { stacks, hasUnmanaged } = stackOptions(all);
  const filtered = (search.stack ?? ALL_STACKS) !== ALL_STACKS || (search.state ?? "all") !== "all" || !!search.q;

  return (
    <div className="grid gap-4">
      <div className="flex flex-wrap items-end gap-3">
        <div className="grid gap-1.5">
          <Label htmlFor="stack-filter">{t("containers.filters.stack")}</Label>
          <Select
            value={search.stack ?? ALL_STACKS}
            onValueChange={(stack) => stack !== "" && setSearch({ stack: stack === ALL_STACKS ? undefined : stack })}
          >
            <SelectTrigger id="stack-filter" className="min-w-44">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={ALL_STACKS}>{t("containers.filters.allStacks")}</SelectItem>
              {stacks.map((stack) => (
                <SelectItem key={stack} value={stack}>
                  {stack}
                </SelectItem>
              ))}
              {hasUnmanaged && <SelectItem value={NO_STACK}>{t("containers.filters.noStack")}</SelectItem>}
            </SelectContent>
          </Select>
        </div>

        <div className="grid gap-1.5">
          <Label htmlFor="state-filter">{t("containers.filters.state")}</Label>
          <Select
            value={search.state ?? "all"}
            onValueChange={(state) => state !== "" && setSearch({ state: state === "all" ? undefined : (state as StateFilter) })}
          >
            <SelectTrigger id="state-filter" className="min-w-36">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {stateFilters.map((state) => (
                <SelectItem key={state} value={state}>
                  {t(`containers.filters.states.${state}`)}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>

        <div className="grid min-w-64 flex-1 gap-1.5">
          <Label htmlFor="container-search">{t("containers.filters.search")}</Label>
          <div className="relative">
            <SearchIcon className="text-muted-foreground pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2" />
            <Input
              id="container-search"
              type="search"
              className="pl-8"
              value={search.q ?? ""}
              onChange={(event) => setSearch({ q: event.target.value === "" ? undefined : event.target.value })}
            />
          </div>
        </div>

        <Button
          variant="ghost"
          disabled={!filtered}
          onClick={() => setSearch({ stack: undefined, state: undefined, q: undefined })}
        >
          <FilterXIcon data-icon="inline-start" />
          {t("containers.filters.reset")}
        </Button>

        <Button variant="outline" disabled={containers.isFetching} onClick={() => void containers.refetch()}>
          {containers.isFetching ? (
            <Loader2Icon data-icon="inline-start" className="animate-spin" />
          ) : (
            <RefreshCwIcon data-icon="inline-start" />
          )}
          {t("containers.refresh")}
        </Button>
      </div>

      <div aria-live="polite">
        {notice && (
          <p role={notice.kind === "error" ? "alert" : "status"} className={notice.kind === "error" ? "text-destructive text-sm" : "text-sm"}>
            {noticeText(notice)}
          </p>
        )}
      </div>

      {containers.isPending ? (
        <p className="text-muted-foreground flex items-center gap-2">
          <Loader2Icon className="animate-spin" />
          {t("containers.loading")}
        </p>
      ) : containers.isError && all.length === 0 ? (
        <p role="alert" className="text-destructive">
          {t("containers.errors.loadFailed")} {errorText(containers.error)}
        </p>
      ) : all.length === 0 ? (
        <p className="text-muted-foreground">{t("containers.empty")}</p>
      ) : (
        <>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("containers.columns.name")}</TableHead>
                <TableHead>{t("containers.columns.stack")}</TableHead>
                <TableHead>{t("containers.columns.state")}</TableHead>
                <TableHead>{t("containers.columns.image")}</TableHead>
                <TableHead>{t("containers.columns.created")}</TableHead>
                <TableHead className="text-right">{t("containers.columns.actions")}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {shown.map((container) => (
                <ContainerRow
                  key={container.id}
                  container={container}
                  busy={action.isPending && action.variables?.containerId === container.id}
                  locked={action.isPending}
                  language={i18n.resolvedLanguage}
                  onAction={onAction}
                />
              ))}
            </TableBody>
          </Table>
          <p className="text-muted-foreground text-sm">
            {shown.length === 0
              ? t("containers.noMatches")
              : t("containers.count", { shown: shown.length, total: all.length })}
            {containers.dataUpdatedAt > 0 && (
              <>
                {" · "}
                {t("containers.updatedAt", {
                  time: new Intl.DateTimeFormat(i18n.resolvedLanguage, { timeStyle: "medium" }).format(containers.dataUpdatedAt),
                })}
              </>
            )}
          </p>
        </>
      )}

      <StopDialog
        pending={pending}
        onCancel={() => setPending(undefined)}
        onConfirm={(timeoutSeconds) => {
          if (pending) {
            run(pending.container, pending.action, timeoutSeconds);
          }

          setPending(undefined);
        }}
      />
    </div>
  );
}

const actionIcons = { start: PlayIcon, stop: SquareIcon, restart: RotateCwIcon } as const;

function ContainerRow({
  container,
  busy,
  locked,
  language,
  onAction,
}: {
  container: Container;
  busy: boolean;
  locked: boolean;
  language: string | undefined;
  onAction: (container: Container, action: ContainerAction) => void;
}) {
  const { t } = useTranslation();
  const group = stateFilterOf(container.state);

  return (
    <TableRow>
      <TableCell>
        <div className="font-medium">{container.name}</div>
        <div className="text-muted-foreground font-mono text-xs">{container.id.slice(0, 12)}</div>
      </TableCell>
      <TableCell>
        {container.stackName ?? <span className="text-muted-foreground">{t("containers.filters.noStack")}</span>}
        {container.serviceName && <div className="text-muted-foreground text-xs">{container.serviceName}</div>}
      </TableCell>
      <TableCell>
        <Badge variant={group === "running" ? "default" : container.state === "Dead" ? "destructive" : "secondary"}>
          {t(`containers.states.${container.state}`)}
        </Badge>
        <div className="text-muted-foreground mt-1 text-xs">{container.statusText}</div>
      </TableCell>
      <TableCell className="max-w-64 truncate font-mono text-xs" title={container.image}>
        {container.image}
      </TableCell>
      <TableCell className="text-sm whitespace-nowrap">
        {container.createdAt &&
          new Intl.DateTimeFormat(language, { dateStyle: "short", timeStyle: "short" }).format(new Date(container.createdAt))}
      </TableCell>
      <TableCell className="text-right">
        {container.canControl ? (
          <div className="flex justify-end gap-1">
            {(["start", "stop", "restart"] as const).map((kind) => {
              const Icon = actionIcons[kind];
              const label = t(`containers.actions.${kind}`);
              return (
                <Button
                  key={kind}
                  variant="ghost"
                  size="icon"
                  aria-label={`${label}: ${container.name}`}
                  title={label}
                  disabled={locked || !isActionAvailable(kind, container)}
                  onClick={() => onAction(container, kind)}
                >
                  {busy ? <Loader2Icon className="animate-spin" /> : <Icon />}
                </Button>
              );
            })}
          </div>
        ) : (
          <span className="text-muted-foreground text-sm">{t("containers.viewOnly")}</span>
        )}
      </TableCell>
    </TableRow>
  );
}

/** Confirms a stop or restart and asks how long Docker may wait for a clean exit. */
function StopDialog({
  pending,
  onCancel,
  onConfirm,
}: {
  pending: PendingAction | undefined;
  onCancel: () => void;
  onConfirm: (timeoutSeconds: number) => void;
}) {
  const { t } = useTranslation();
  const [timeout, setTimeoutText] = useState(String(DEFAULT_STOP_TIMEOUT));
  const seconds = parseStopTimeout(timeout);

  return (
    <AlertDialog
      open={pending !== undefined}
      onOpenChange={(open) => {
        if (!open) {
          setTimeoutText(String(DEFAULT_STOP_TIMEOUT));
          onCancel();
        }
      }}
    >
      {pending && (
        <AlertDialogContent>
          <form
            className="grid gap-4"
            onSubmit={(event) => {
              event.preventDefault();
              if (seconds !== null) {
                setTimeoutText(String(DEFAULT_STOP_TIMEOUT));
                onConfirm(seconds);
              }
            }}
          >
            <AlertDialogHeader>
              <AlertDialogTitle>{t(`containers.confirm.${pending.action}Title`, { name: pending.container.name })}</AlertDialogTitle>
              <AlertDialogDescription>{t(`containers.confirm.${pending.action}Description`)}</AlertDialogDescription>
            </AlertDialogHeader>
            <div className="grid gap-1.5">
              <Label htmlFor="stop-timeout">{t("containers.confirm.timeout")}</Label>
              <Input
                id="stop-timeout"
                inputMode="numeric"
                className="w-28"
                value={timeout}
                aria-invalid={seconds === null}
                aria-describedby="stop-timeout-hint"
                onChange={(event) => setTimeoutText(event.target.value)}
              />
              <p id="stop-timeout-hint" className={seconds === null ? "text-destructive text-sm" : "text-muted-foreground text-sm"}>
                {seconds === null
                  ? t("containers.confirm.timeoutInvalid", { max: MAX_STOP_TIMEOUT })
                  : t("containers.confirm.timeoutHint", { max: MAX_STOP_TIMEOUT })}
              </p>
            </div>
            <AlertDialogFooter>
              <AlertDialogCancel type="button">{t("containers.confirm.cancel")}</AlertDialogCancel>
              <Button type="submit" variant={pending.action === "stop" ? "destructive" : "default"} disabled={seconds === null}>
                {t(`containers.actions.${pending.action}`)}
              </Button>
            </AlertDialogFooter>
          </form>
        </AlertDialogContent>
      )}
    </AlertDialog>
  );
}

function ContainersLoadError() {
  const { t } = useTranslation();

  return (
    <p role="alert" className="text-destructive">
      {t("containers.errors.loadEnvironmentsFailed")}
    </p>
  );
}
