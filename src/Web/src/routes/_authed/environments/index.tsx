import { useQuery } from "@tanstack/react-query";
import { Link, createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { environmentsQuery } from "@/api/queries";
import { CreateEnvironmentDialog, EditEnvironmentDialog } from "@/components/environments/environment-dialogs";
import { ErrorText } from "@/components/form-messages";
import { FilterXIcon, LockIcon, Loader2Icon, RefreshCwIcon, SearchIcon } from "@/components/icons";
import { useRememberListSearch } from "@/components/navigation/use-remember-list-search";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import {
  type AgentStatus,
  type Environment,
  type EnvironmentsSearch,
  STATUS_REFRESH_MS,
  agentStatusOf,
  agentStatuses,
  describeError,
  environmentsSearchSchema,
  filterEnvironments,
  statusVariant,
  tokenStateOf,
} from "@/features/environments/model";
import { formatDateTime } from "@/lib/format";
import { rowActionsClass, rowClass, rowLinkClass } from "@/lib/row-link";
import { cn } from "@/lib/utils";
import { useLiveStatus } from "@/components/live/hub-provider";
import { pollingInterval } from "@/features/live/model";
import { MetricsSettingsCard } from "@/components/metrics/metrics-settings-card";

export const Route = createFileRoute("/_authed/environments/")({
  validateSearch: environmentsSearchSchema,
  loader: ({ context }) => context.queryClient.ensureQueryData(environmentsQuery),
  component: EnvironmentsScreen,
});

/** Every environment the user may see with its agent's status; administrators also create and edit them. */
function EnvironmentsScreen() {
  const { t, i18n } = useTranslation();
  const { me } = Route.useRouteContext();
  const search = Route.useSearch();
  useRememberListSearch("environments", search);
  const navigate = Route.useNavigate();
  const live = useLiveStatus();
  const environments = useQuery({ ...environmentsQuery, refetchInterval: pollingInterval(live, STATUS_REFRESH_MS) });

  const setSearch = (patch: Partial<EnvironmentsSearch>) => {
    // Navigating to the same search would only re-run the loader.
    if (Object.entries(patch).every(([key, value]) => search[key as keyof EnvironmentsSearch] === value)) {
      return;
    }

    void navigate({ search: (previous) => ({ ...previous, ...patch }), replace: true });
  };

  const all = environments.data ?? [];
  const shown = filterEnvironments(all, search);
  const filtered = search.status !== undefined || !!search.q;
  const error = environments.error ? describeError(environments.error) : undefined;

  return (
    <div className="grid gap-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="font-heading text-2xl font-semibold">{t("environments.title")}</h1>
          <p className="text-muted-foreground mt-1">{t("environments.description")}</p>
        </div>
        {/* Only a convenience: the aggregator refuses non-administrators anyway. */}
        {me.isAdmin && <CreateEnvironmentDialog />}
      </div>

      <div className="flex flex-wrap items-end gap-3">
        <div className="grid gap-1.5">
          <Label htmlFor="environment-status">{t("environments.filters.status")}</Label>
          <Select
            value={search.status ?? "all"}
            // Radix Select also reports its current value, or "" while it matches no item; neither is a choice.
            onValueChange={(value) =>
              value !== "" && setSearch({ status: value === "all" ? undefined : (value as AgentStatus) })
            }
          >
            <SelectTrigger id="environment-status" className="min-w-44">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">{t("environments.filters.allStatuses")}</SelectItem>
              {agentStatuses.map((status) => (
                <SelectItem key={status} value={status}>
                  {t(`environments.status.${status}`)}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>

        <div className="grid min-w-64 flex-1 gap-1.5">
          <Label htmlFor="environment-search">{t("environments.filters.search")}</Label>
          <div className="relative">
            <SearchIcon className="text-muted-foreground pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2" />
            <Input
              id="environment-search"
              type="search"
              className="pl-8"
              value={search.q ?? ""}
              onChange={(event) => setSearch({ q: event.target.value === "" ? undefined : event.target.value })}
            />
          </div>
        </div>

        <Button variant="ghost" disabled={!filtered} onClick={() => setSearch({ status: undefined, q: undefined })}>
          <FilterXIcon data-icon="inline-start" />
          {t("environments.filters.reset")}
        </Button>

        <Button variant="outline" disabled={environments.isFetching} onClick={() => void environments.refetch()}>
          {environments.isFetching ? (
            <Loader2Icon data-icon="inline-start" className="animate-spin" />
          ) : (
            <RefreshCwIcon data-icon="inline-start" />
          )}
          {t("environments.refresh")}
        </Button>
      </div>

      {error && all.length > 0 && (
        <ErrorText>
          {t("environments.errors.refreshFailed")} {t(error.key)} {error.detail}
        </ErrorText>
      )}

      {environments.isPending ? (
        <p className="text-muted-foreground flex items-center gap-2">
          <Loader2Icon className="animate-spin" />
          {t("environments.loading")}
        </p>
      ) : error && all.length === 0 ? (
        <ErrorText>
          {t("environments.errors.loadFailed")} {t(error.key)} {error.detail}
        </ErrorText>
      ) : all.length === 0 ? (
        <p className="text-muted-foreground">{me.isAdmin ? t("environments.emptyAdmin") : t("environments.empty")}</p>
      ) : (
        <>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t("environments.columns.name")}</TableHead>
                <TableHead>{t("environments.columns.status")}</TableHead>
                <TableHead className="hidden md:table-cell">{t("environments.columns.host")}</TableHead>
                <TableHead className="hidden lg:table-cell">{t("environments.columns.version")}</TableHead>
                <TableHead className="hidden sm:table-cell">{t("environments.columns.lastSeen")}</TableHead>
                {me.isAdmin && <TableHead className="text-right">{t("environments.columns.actions")}</TableHead>}
              </TableRow>
            </TableHeader>
            <TableBody>
              {shown.map((environment) => (
                <EnvironmentRow key={environment.id} environment={environment} isAdmin={me.isAdmin} language={i18n.resolvedLanguage} />
              ))}
            </TableBody>
          </Table>
          <p className="text-muted-foreground text-sm">
            {shown.length === 0 ? t("environments.noMatches") : t("environments.count", { shown: shown.length, total: all.length })}
            {environments.dataUpdatedAt > 0 && (
              <>
                {" · "}
                {t("environments.updatedAt", {
                  time: new Intl.DateTimeFormat(i18n.resolvedLanguage, { timeStyle: "medium" }).format(environments.dataUpdatedAt),
                })}
              </>
            )}
          </p>
        </>
      )}
      {me.isAdmin && <MetricsSettingsCard />}
    </div>
  );
}

function EnvironmentRow({
  environment,
  isAdmin,
  language,
}: {
  environment: Environment;
  isAdmin: boolean;
  language: string | undefined;
}) {
  const { t } = useTranslation();
  const status = agentStatusOf(environment);
  const token = tokenStateOf(environment, new Date());

  return (
    <TableRow className={rowClass}>
      <TableCell>
        <Link
          to="/environments/$environmentId"
          params={{ environmentId: environment.id }}
          className={cn("font-medium", rowLinkClass)}
        >
          {environment.name}
        </Link>
        {environment.isPrivate && (
          <span className="text-muted-foreground ml-2 inline-flex items-center gap-1 text-xs">
            <LockIcon className="size-3.5" />
            {t("environments.private")}
          </span>
        )}
      </TableCell>
      <TableCell>
        <Badge variant={statusVariant(status)}>{t(`environments.status.${status}`)}</Badge>
        {status === "notEnrolled" && token !== "none" && (
          <div className="text-muted-foreground mt-1 text-xs">{t(`environments.token.${token}`)}</div>
        )}
      </TableCell>
      <TableCell className="hidden font-mono text-xs md:table-cell">{environment.agentHostname ?? "—"}</TableCell>
      <TableCell className="hidden text-sm lg:table-cell">{environment.agentVersion ?? "—"}</TableCell>
      <TableCell className="hidden text-sm whitespace-nowrap sm:table-cell">
        {status === "online"
          ? t("environments.connectedNow")
          : environment.lastSeenAt
            ? formatDateTime(environment.lastSeenAt, language)
            : "—"}
      </TableCell>
      {isAdmin && (
        <TableCell className={cn("text-right", rowActionsClass)}>
          <EditEnvironmentDialog environment={environment} />
        </TableCell>
      )}
    </TableRow>
  );
}
