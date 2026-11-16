import { useQuery, useSuspenseQuery } from "@tanstack/react-query";
import { createFileRoute, redirect } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { containersQuery, environmentsQuery } from "@/api/queries";
import { FilterXIcon, Loader2Icon, RefreshCwIcon, SearchIcon } from "@/components/icons";
import { useRememberListSearch } from "@/components/navigation/use-remember-list-search";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { ContainerTable } from "@/components/containers/container-table";
import {
  ALL_STACKS,
  type ContainersSearch,
  type Environment,
  NO_STACK,
  type StateFilter,
  containersSearchSchema,
  describeError,
  filterContainers,
  stackOptions,
  stateFilters,
} from "@/features/containers/model";
import { useLiveStatus } from "@/components/live/hub-provider";
import { pollingInterval } from "@/features/live/model";

/**
 * Without live updates the list is reloaded this often, so the states do not go stale while the screen is open.
 * With them the agent reports container events; the slow reload covers agents too old to send them.
 */
const REFRESH_INTERVAL_MS = 15_000;
const REFRESH_WHILE_LIVE_MS = 60_000;

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
  useRememberListSearch("containers", search);
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
          <h1 className="font-heading flex flex-wrap items-baseline gap-x-3 text-2xl font-semibold">
            {t("containers.title")}
            {environment && <span className="text-muted-foreground text-lg font-medium">{environment.name}</span>}
          </h1>
          <p className="text-muted-foreground mt-1">{t("containers.description")}</p>
        </div>
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
  const live = useLiveStatus();
  const containers = useQuery({
    ...containersQuery(environment.id),
    // An offline agent answers 503 every time; there is nothing to load until it reconnects.
    enabled: environment.isOnline,
    refetchInterval: pollingInterval(live, REFRESH_INTERVAL_MS, REFRESH_WHILE_LIVE_MS),
  });
  const errorText = (error: unknown) => {
    const { key, detail } = describeError(error);
    return detail ? `${t(key)} ${detail}` : t(key);
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
          {shown.length > 0 && <ContainerTable environmentId={environment.id} containers={shown} />}
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

    </div>
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
